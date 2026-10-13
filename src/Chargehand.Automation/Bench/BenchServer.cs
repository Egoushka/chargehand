using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;

namespace Chargehand.Bench;

/// <summary>The benchmark could not go on: a refusal from the server, a lost batch, an unreadable set.</summary>
public sealed class BenchException(string message) : Exception(message);

/// <summary>A server that runs driven batches, as the benchmark uses it: the same HTTP API as any caller (<c>POST /v1/runs</c>, the run's events, its result).</summary>
public interface IBenchServer
{
    /// <returns>The batch's run id.</returns>
    Task<string> SubmitAsync(RunRequest request, CancellationToken ct);

    /// <summary>Follows the run's events until its result, passing each to <paramref name="onEvent"/>.</summary>
    Task<(IReadOnlyList<RunStatus> Events, ResultContract Result)> FollowAsync(string runId, Action<RunStatus>? onEvent, CancellationToken ct);

    /// <returns>Null when the server has no finished result for the run.</returns>
    Task<ResultContract?> GetResultAsync(string runId, CancellationToken ct);
}

public sealed class BenchServer(HttpClient http, TimeSpan? retryDelay = null, TimeSpan? pollEvery = null) : IBenchServer
{
    private const int Attempts = 5;

    public async Task<string> SubmitAsync(RunRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/runs") { Content = JsonContent.Create(request, options: ContractJson.Options) };
        message.Headers.TryAddWithoutValidation("Prefer", "wait=1");
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                throw new BenchException($"the server refused the API key (HTTP {(int)response.StatusCode}).");
            case HttpStatusCode.BadRequest:
                throw new BenchException($"the server refused the request: {await Truncated(response, ct)}");
            case HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests:
                throw new BenchException($"the server cannot take a batch now (HTTP {(int)response.StatusCode}): {await Truncated(response, ct)}");
        }
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Accepted)
            throw new BenchException($"the server answered HTTP {(int)response.StatusCode}: {await Truncated(response, ct)}");
        var location = response.Headers.Location?.ToString();
        return location is { Length: > 0 } && location.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1] is { Length: > 0 } id
            ? id
            : throw new BenchException("the server accepted the batch but named no run id.");
    }

    public async Task<(IReadOnlyList<RunStatus> Events, ResultContract Result)> FollowAsync(string runId, Action<RunStatus>? onEvent, CancellationToken ct)
    {
        List<RunStatus> events = [];
        var seen = 0;
        var reason = "";
        for (var failures = 0; failures < Attempts;)
        {
            var before = seen;
            try
            {
                if (failures > 0)
                    await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(2), ct);
                using var response = await http.GetAsync($"/v1/runs/{Uri.EscapeDataString(runId)}/events", HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new BenchException($"the server refused the API key (HTTP {(int)response.StatusCode}).");
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new BenchException($"no run {runId} on this server.");
                if (!response.IsSuccessStatusCode)
                    reason = $"the server answered HTTP {(int)response.StatusCode}";
                else
                {
                    await using var body = await response.Content.ReadAsStreamAsync(ct);
                    var index = 0;
                    await foreach (var item in SseParser.Create(body, (_, bytes) => Encoding.UTF8.GetString(bytes)).EnumerateAsync(ct))
                    {
                        // The events replay from the start on every connection; the ones already seen are skipped.
                        if (index++ < seen)
                            continue;
                        seen = index;
                        var e = JsonSerializer.Deserialize<RunStatus>(item.Data, ContractJson.Options)!;
                        events.Add(e);
                        onEvent?.Invoke(e);
                        if (e.Status == RunState.Lost)
                            throw new BenchException($"batch {runId} was lost: {e.Detail ?? "the process that ran it ended"}.");
                        if (e.Result is { } result)
                            return (events, result);
                    }
                    reason = "the stream ended before the run did";
                }
            }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException)
            {
                reason = e.Message;
            }
            failures = seen > before ? 0 : failures + 1;
        }
        // The progress events are gone with the connection, but the result is not: take that, and say the tally may be short.
        var late = await PollAsync(runId, reason, ct);
        return (events, late);
    }

    public async Task<ResultContract?> GetResultAsync(string runId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"/v1/runs/{Uri.EscapeDataString(runId)}", ct);
        if (response.StatusCode == HttpStatusCode.Gone)
            throw new BenchException($"batch {runId} was lost: the process that ran it ended.");
        return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<ResultContract>(ContractJson.Options, ct) : null;
    }

    private async Task<ResultContract> PollAsync(string runId, string reason, CancellationToken ct)
    {
        for (var i = 0; i < 4 * 60 * 12; i++)
        {
            try
            {
                if (await GetResultAsync(runId, ct) is { } result)
                    return result;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException)
            {
                reason = e.Message;
            }
            await Task.Delay(pollEvery ?? TimeSpan.FromSeconds(5), ct);
        }
        throw new BenchException($"lost the connection to batch {runId} ({reason}) and it has no result after 4 hours; `chargehand watch {runId}` shows where it is.");
    }

    private static async Task<string> Truncated(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        return text.Length > 600 ? text[..600] + "..." : text;
    }
}
