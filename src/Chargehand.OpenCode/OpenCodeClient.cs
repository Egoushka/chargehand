using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;

namespace Chargehand.OpenCode;

/// <summary>An OpenCode error response: HTTP status plus the tagged error body (e.g. InstructionEntryValueTooLargeError).</summary>
public sealed class OpenCodeException(HttpStatusCode status, string? tag, string message)
    : ChargehandException(CodeOf(status, message), $"OpenCode {(int)status} {tag}: {message}", ActionOf(message))
{
    /// <summary>What to do when the gateway refuses a call over a spend budget (LiteLLM: "Budget has been exceeded!").</summary>
    public const string BudgetAction = "The model gateway refused the call over a spend budget: raise the gateway key's budget, or wait for its reset.";

    public HttpStatusCode Status { get; } = status;
    public string? Tag { get; } = tag;

    /// <summary>OpenCode relays a gateway's rate limit, and a provider it cannot reach, as a 503 ServiceUnavailableError.</summary>
    private static ErrorCode CodeOf(HttpStatusCode status, string message) =>
        status == HttpStatusCode.TooManyRequests || RateLimited(message) ? ErrorCode.RateLimited
        : status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout ? ErrorCode.ProviderUnavailable
        : ErrorCode.Internal;

    private static string? ActionOf(string message) => message.Contains("budget", StringComparison.OrdinalIgnoreCase) ? BudgetAction : null;
}

/// <summary>Hand-written client for the adapter's operations (docs/opencode-adapter-ops.json).</summary>
public sealed class OpenCodeClient : IOpenCodeClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    /// <param name="http">BaseAddress = the server URL; no timeout (wait long-polls; callers pass deadlines).</param>
    public OpenCodeClient(HttpClient http, string password)
    {
        _http = http;
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + password)));
    }

    public async Task<string> VersionAsync(CancellationToken ct) =>
        (await Send<JsonElement>(HttpMethod.Get, "/api/info", null, ct)).GetProperty("version").GetString()!;

    public async Task<SessionInfo> CreateSessionAsync(CreateSessionBody body, CancellationToken ct) =>
        await Data<SessionInfo>(HttpMethod.Post, "/api/session", body, ct);

    public Task<SessionInfo> GetSessionAsync(string sessionId, CancellationToken ct) =>
        Data<SessionInfo>(HttpMethod.Get, $"/api/session/{sessionId}", null, ct);

    public Task PromptAsync(string sessionId, string text, CancellationToken ct) =>
        Send<JsonElement>(HttpMethod.Post, $"/api/session/{sessionId}/prompt", new { text }, ct);

    public Task WaitAsync(string sessionId, CancellationToken ct) =>
        Send<JsonElement>(HttpMethod.Post, $"/api/experimental/session/{sessionId}/wait", null, ct);

    public async Task<IReadOnlyList<JsonElement>> MessagesAsync(string sessionId, CancellationToken ct) =>
        [.. (await Data<JsonElement>(HttpMethod.Get, $"/api/session/{sessionId}/message", null, ct)).EnumerateArray()];

    public async Task<bool> InterruptAsync(string sessionId, CancellationToken ct) =>
        (await Send<JsonElement>(HttpMethod.Post, $"/api/session/{sessionId}/interrupt", null, ct)).GetProperty("interrupted").GetBoolean();

    public Task<SessionInfo> ForkAsync(string sessionId, string? beforeMessageId, CancellationToken ct) =>
        Data<SessionInfo>(HttpMethod.Post, $"/api/session/{sessionId}/fork", new { before = beforeMessageId }, ct);

    public Task CompactAsync(string sessionId, CancellationToken ct) =>
        Send<JsonElement>(HttpMethod.Post, $"/api/session/{sessionId}/compact", new { delivery = "steer" }, ct);

    public async Task<IReadOnlyList<JsonElement>> PermissionsAsync(string sessionId, CancellationToken ct) =>
        [.. (await Data<JsonElement>(HttpMethod.Get, $"/api/session/{sessionId}/permission", null, ct)).EnumerateArray()];

    public Task ReplyPermissionAsync(string sessionId, string requestId, string decision, string? message, CancellationToken ct) =>
        decision is "once" or "reject"
            ? Send<JsonElement>(HttpMethod.Post, $"/api/session/{sessionId}/permission/{requestId}/reply", new { decision, message }, ct)
            : throw new ArgumentException("only 'once' or 'reject' (ADR 0006)", nameof(decision));

    public async Task<IReadOnlyList<JsonElement>> DiffAsync(string sessionId, CancellationToken ct) =>
        [.. (await Data<JsonElement>(HttpMethod.Get, $"/api/session/{sessionId}/diff", null, ct)).EnumerateArray()];

    public Task PutInstructionAsync(string sessionId, string key, string value, CancellationToken ct) =>
        Send<JsonElement>(HttpMethod.Put, $"/api/experimental/session/{sessionId}/instructions/entries/{Uri.EscapeDataString(key)}", new { value }, ct);

    public Task MoveAsync(string sessionId, string directory, CancellationToken ct) =>
        Send<JsonElement>(HttpMethod.Post, $"/api/session/{sessionId}/move", new { directory }, ct);

    /// <summary>
    /// Stateless, so safe to repeat: one retry when OpenCode relays a 503 (the masking proxy's or a dropped gateway
    /// connection, ADR 0011). Session prompts are not repeated this way; a second submit would run the turn twice.
    /// </summary>
    public async Task<string> GenerateAsync(string? providerId, string? modelId, string prompt, CancellationToken ct)
    {
        var model = providerId is null ? null : new ModelBody(providerId, modelId!);
        for (var attempt = 1; ; attempt++)
            try
            {
                return (await Data<JsonElement>(HttpMethod.Post, "/api/experimental/generate", new { prompt, model }, ct))
                    .GetProperty("text").GetString()!;
            }
            catch (OpenCodeException e) when (attempt == 1 && e.Status == HttpStatusCode.ServiceUnavailable)
            {
                await Task.Delay(GenerateRetryDelay, ct);
            }
    }

    internal static TimeSpan GenerateRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    public async IAsyncEnumerable<JsonElement> EventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/event");
        using var res = await SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureOk(res, ct);
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
            if (line.StartsWith("data:", StringComparison.Ordinal))
                yield return JsonDocument.Parse(line.AsMemory(5)).RootElement.Clone();
    }

    private async Task<T> Data<T>(HttpMethod method, string path, object? body, CancellationToken ct) =>
        (await Send<JsonElement>(method, path, body, ct)).GetProperty("data").Deserialize<T>(Json)!;

    /// <summary>
    /// The first request to a location that is still booting sees no providers and fails with
    /// 400 "Model unavailable" about a second before they load (observed on 2.0.16); that one case is retried.
    /// </summary>
    private async Task<T> Send<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, path);
            if (body is not null)
                req.Content = JsonContent.Create(body, options: Json);
            using var res = await SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            try
            {
                await EnsureOk(res, ct);
            }
            catch (OpenCodeException e) when (attempt < ModelBootAttempts && e.Status == HttpStatusCode.BadRequest && e.Message.Contains("Model unavailable", StringComparison.Ordinal))
            {
                await Task.Delay(ModelBootDelay, ct);
                continue;
            }
            if (res.StatusCode == HttpStatusCode.NoContent || res.Content.Headers.ContentLength == 0)
                return default!;
            return (await res.Content.ReadFromJsonAsync<T>(Json, ct))!;
        }
    }

    /// <summary>A refused connection means no OpenCode server listens at the URL: the action says how to start one.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, HttpCompletionOption completion, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(req, completion, ct);
        }
        catch (HttpRequestException e) when (e.HttpRequestError == HttpRequestError.ConnectionError)
        {
            throw new ChargehandException(ErrorCode.RuntimeUnavailable, $"the OpenCode server at {_http.BaseAddress} is not reachable: {e.Message}",
                $"Start it: scripts/opencode-serve.sh <binary> <config> {_http.BaseAddress?.Port}");
        }
    }

    internal static int ModelBootAttempts { get; set; } = 10;

    internal static TimeSpan ModelBootDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    private static async Task EnsureOk(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode)
            return;
        var text = await res.Content.ReadAsStringAsync(ct);
        string? tag = null, message = text;
        try
        {
            var e = JsonDocument.Parse(text).RootElement;
            tag = e.TryGetProperty("_tag", out var t) ? t.GetString() : e.TryGetProperty("name", out var n) ? n.GetString() : null;
            message = e.TryGetProperty("message", out var m) ? m.GetString() : text;
        }
        catch (JsonException)
        {
        }
        throw new OpenCodeException(res.StatusCode, tag, message ?? "");
    }
}
