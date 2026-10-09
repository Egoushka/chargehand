using System.Net;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;

namespace Chargehand.Watch;

/// <summary><c>chargehand watch &lt;run-id&gt;</c> and <c>chargehand cancel &lt;run-id&gt;</c>: a run on a server from a terminal or from the Monitor tool of a Claude Code session
/// that handed the work over. <c>watch</c> tails <c>GET /v1/runs/{id}/events</c> and prints one stdout line per event until the result, because a Monitor turns each line into a
/// notification and sees nothing on stderr. The exit code is the run's: 0 completed, 1 failed, denied, lost or the server gone, 3 needs input, 2 a refusal (wrong key or unknown run).
/// The events replay from the start on every connection, so a dropped connection resumes by skipping the lines already printed.</summary>
public static class WatchCli
{
    public const string Usage = "usage: chargehand watch <run-id> [--url <server>]   |   chargehand cancel <run-id> [--url <server>]\n" +
        "  the server comes from --url or CHARGEHAND_URL, the key from CHARGEHAND_API_KEY; either missing falls back to the profile's http block";

    /// <summary>Consecutive tries that bring no new event before <c>watch</c> gives up.</summary>
    public const int Attempts = 5;

    private static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(30);

    public static (HttpClient? Client, string? Problem) Connect(IReadOnlyList<string> args, Func<string, string?> env, Func<(string Url, string Key)?> profile)
    {
        string? url = null;
        for (var i = 0; i < args.Count; i += 2)
        {
            if (args[i] != "--url" || i + 1 >= args.Count)
                return (null, Usage);
            url = args[i + 1];
        }
        url ??= env("CHARGEHAND_URL");
        var key = env("CHARGEHAND_API_KEY");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
        {
            var fallback = profile();
            url = string.IsNullOrEmpty(url) ? fallback?.Url : url;
            key = string.IsNullOrEmpty(key) ? fallback?.Key : key;
        }
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
            return (null, "no server or key: pass --url <server> (or set CHARGEHAND_URL) and set CHARGEHAND_API_KEY, or name a profile with an http block.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return (null, $"the server must be an absolute http or https URL, got '{url}'.");
        var client = new HttpClient { BaseAddress = uri, Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new("Bearer", key);
        return (client, null);
    }

    public static async Task<int> WatchAsync(HttpClient http, string runId, TextWriter output, TextWriter error, CancellationToken ct, TimeProvider? time = null, TimeSpan? retryDelay = null)
    {
        time ??= TimeProvider.System;
        var printer = new LinePrinter(output, time);
        var seen = 0;
        string reason = "";
        for (var failures = 0; failures < Attempts;)
        {
            var before = seen;
            try
            {
                if (failures > 0)
                    await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(2), ct);
                using var response = await http.GetAsync($"/v1/runs/{Uri.EscapeDataString(runId)}/events", HttpCompletionOption.ResponseHeadersRead, ct);
                if (Refusal(response.StatusCode, runId) is { } refused)
                {
                    await error.WriteLineAsync(refused);
                    return 2;
                }
                if (!response.IsSuccessStatusCode)
                {
                    reason = $"the server answered HTTP {(int)response.StatusCode}";
                }
                else
                {
                    await using var body = await response.Content.ReadAsStreamAsync(ct);
                    var index = 0;
                    await foreach (var item in SseParser.Create(body, (_, bytes) => Encoding.UTF8.GetString(bytes)).EnumerateAsync(ct))
                    {
                        if (index++ < seen)
                            continue;
                        seen = index;
                        var e = JsonSerializer.Deserialize<RunStatus>(item.Data, ContractJson.Options)!;
                        if (printer.Print(e) is { } exit)
                            return exit;
                    }
                    reason = "the stream ended before the run did";
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await output.WriteLineAsync($"watch stopped; run {runId} keeps going (chargehand cancel {runId} stops it)");
                return 1;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException)
            {
                reason = e.Message;
            }
            failures = seen > before ? 0 : failures + 1;
        }
        await output.WriteLineAsync($"watch gave up after {Attempts} tries: {reason}. Run {runId} is not cancelled; `chargehand watch {runId}` picks it up again.");
        return 1;
    }

    public static async Task<int> CancelAsync(HttpClient http, string runId, TextWriter output, TextWriter error, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsync($"/v1/runs/{Uri.EscapeDataString(runId)}/cancel", null, ct);
            if (Refusal(response.StatusCode, runId) is { } refused)
            {
                await error.WriteLineAsync(refused);
                return 2;
            }
            if (!response.IsSuccessStatusCode)
            {
                await error.WriteLineAsync($"the server answered HTTP {(int)response.StatusCode}; run {runId} may still be going.");
                return 1;
            }
            await output.WriteLineAsync($"cancelling {runId}: a session container stops within its grace period; `chargehand watch {runId}` shows the end.");
            return 0;
        }
        catch (HttpRequestException e)
        {
            await error.WriteLineAsync($"could not reach the server: {e.Message}");
            return 1;
        }
    }

    private static string? Refusal(HttpStatusCode status, string runId) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"the server refused the API key (HTTP {(int)status}).",
        HttpStatusCode.NotFound => $"no run {runId} on this server.",
        _ => null,
    };

    /// <summary>One line per event; session progress is thinned so a long task does not flood a Monitor, and a new stage always shows.</summary>
    private sealed class LinePrinter(TextWriter output, TimeProvider time)
    {
        private readonly Dictionary<string, (DateTimeOffset At, SessionStage? Stage)> _progress = [];

        /// <summary>The exit code when <paramref name="e"/> ends the run.</summary>
        public int? Print(RunStatus e)
        {
            if (e.Result is { } result)
            {
                var status = JsonNamingPolicy.SnakeCaseLower.ConvertName(result.Status.ToString());
                output.WriteLine($"run_finished: {status}: {result.Summary}");
                if (result.Error is { } error)
                    output.WriteLine($"  {error.Code}: {error.Message}{(error.Action is { } action ? $" Next: {action}" : "")}");
                return result.Status switch { ResultStatus.Completed => 0, ResultStatus.NeedsInput => 3, _ => 1 };
            }
            if (e.Status == RunState.Lost)
            {
                output.WriteLine($"lost: {e.Detail ?? "the process that ran it ended"}");
                return 1;
            }
            if (e.Event == RunEventKind.SessionProgress && e.TaskId is { } task && !Due(task, e.Stage))
                return null;
            var kind = JsonNamingPolicy.SnakeCaseLower.ConvertName((e.Event ?? RunEventKind.Started).ToString());
            var detail = e.Detail is { Length: > 0 } d ? $": {d}" : "";
            var link = e.PrUrl is { } url && !detail.Contains(url, StringComparison.Ordinal) ? $" {url}" : "";
            output.WriteLine($"{kind}{detail}{link}");
            return null;
        }

        private bool Due(string task, SessionStage? stage)
        {
            var now = time.GetUtcNow();
            if (_progress.TryGetValue(task, out var last) && last.Stage == stage && now - last.At < ProgressEvery)
                return false;
            _progress[task] = (now, stage);
            return true;
        }
    }
}
