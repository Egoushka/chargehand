using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chargehand.Kernel;

namespace Chargehand.Automation;

/// <summary>One prompt of a batch, named by its <paramref name="CustomId"/> in the results.</summary>
public sealed record BatchPrompt(string CustomId, string Prompt);

/// <summary>The answer to one prompt: its text and tokens when it succeeded, else <paramref name="Error"/> (errored, expired or canceled, with the provider's message).</summary>
public sealed record BatchAnswer(string CustomId, string? Text, TokenCounts Tokens, string? Error = null);

/// <summary>What a poll saw: the batch is still processing (no answers), or it ended with an answer for each prompt.</summary>
public sealed record BatchPoll(bool Ended, IReadOnlyList<BatchAnswer> Answers);

/// <summary>A provider's message batch endpoint (ADR 0087): submit prompts once, poll until the batch ended.</summary>
public interface IMessageBatches
{
    /// <returns>The batch id the provider gave.</returns>
    /// <exception cref="IntakeException">The provider refused or could not be reached; the text never carries the key.</exception>
    Task<string> SubmitAsync(string model, int maxTokens, IReadOnlyList<BatchPrompt> prompts, CancellationToken ct);

    /// <exception cref="IntakeException">The provider refused or could not be reached.</exception>
    Task<BatchPoll> PollAsync(string batchId, CancellationToken ct);
}

/// <summary>Anthropic Message Batches over HTTP: <c>POST /v1/messages/batches</c>, <c>GET /v1/messages/batches/{id}</c> and, once <c>processing_status</c> is <c>ended</c>,
/// the JSONL of <c>GET /v1/messages/batches/{id}/results</c>. Every call goes to <paramref name="baseUrl"/> from the profile, never to a <c>results_url</c> the answer names, so a gateway
/// in front of the provider sees all of them. JSON is read and written as nodes: no reflection.</summary>
public sealed class AnthropicMessageBatches(string baseUrl, Func<string> key, HttpClient? client = null) : IMessageBatches
{
    public const string ApiVersion = "2023-06-01";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _client = client ?? new HttpClient { Timeout = Timeout };
    private readonly string _root = baseUrl.TrimEnd('/') + "/v1/messages/batches";

    public async Task<string> SubmitAsync(string model, int maxTokens, IReadOnlyList<BatchPrompt> prompts, CancellationToken ct)
    {
        var requests = new JsonArray();
        foreach (var prompt in prompts)
            requests.Add(new JsonObject
            {
                ["custom_id"] = prompt.CustomId,
                ["params"] = new JsonObject
                {
                    ["model"] = model,
                    ["max_tokens"] = maxTokens,
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt.Prompt }),
                },
            });
        var body = new JsonObject { ["requests"] = requests }.ToJsonString();
        var answer = Parse(await SendAsync(HttpMethod.Post, _root, body, ct));
        return answer?["id"]?.GetValue<string>() is { Length: > 0 } id ? id : throw new IntakeException("the batch endpoint answered without a batch id");
    }

    public async Task<BatchPoll> PollAsync(string batchId, CancellationToken ct)
    {
        var id = Uri.EscapeDataString(batchId);
        var status = Parse(await SendAsync(HttpMethod.Get, $"{_root}/{id}", null, ct))?["processing_status"]?.GetValue<string>();
        if (status != "ended")
            return new BatchPoll(false, []);
        var lines = await SendAsync(HttpMethod.Get, $"{_root}/{id}/results", null, ct);
        return new BatchPoll(true, [.. lines.Split('\n').Where(l => l.Trim().Length > 0).Select(Answer)]);
    }

    internal static BatchAnswer Answer(string line)
    {
        var node = Parse(line) ?? throw new IntakeException("the batch results hold an empty line");
        var customId = node["custom_id"]?.GetValue<string>() ?? "";
        var result = node["result"];
        if (result?["type"]?.GetValue<string>() is not "succeeded")
        {
            var why = result?["error"]?["error"]?["message"]?.GetValue<string>() ?? result?["error"]?["message"]?.GetValue<string>();
            return new BatchAnswer(customId, null, new TokenCounts(0, 0, 0, 0, 0), why is null ? result?["type"]?.GetValue<string>() ?? "no result" : $"{result?["type"]}: {why}");
        }
        var message = result["message"];
        var text = string.Concat((message?["content"]?.AsArray() ?? []).Where(c => c?["type"]?.GetValue<string>() == "text").Select(c => c!["text"]?.GetValue<string>()));
        var usage = message?["usage"];
        return new BatchAnswer(customId, text, new TokenCounts(Count(usage, "input_tokens"), Count(usage, "output_tokens"), 0, Count(usage, "cache_read_input_tokens"), Count(usage, "cache_creation_input_tokens")));
    }

    private static long Count(JsonNode? usage, string name) => usage?[name]?.GetValueKind() == JsonValueKind.Number ? usage[name]!.GetValue<long>() : 0;

    private async Task<string> SendAsync(HttpMethod method, string url, string? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("x-api-key", key());
        request.Headers.Add("anthropic-version", ApiVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        try
        {
            using var response = await _client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new IntakeException($"the batch endpoint answered {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}", (int)response.StatusCode);
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new IntakeException($"the batch endpoint was not reached ({e.GetType().Name})", e is HttpRequestException { StatusCode: { } code } ? (int)code : null);
        }
    }

    private static JsonNode? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException e)
        {
            throw new IntakeException($"the batch endpoint answered JSON that does not parse ({e.Message})");
        }
    }
}
