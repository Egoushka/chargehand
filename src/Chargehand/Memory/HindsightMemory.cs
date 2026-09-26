using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Memory;

/// <summary>
/// Adapter for a self-hosted Hindsight agent-memory service (HTTP API 0.10.0). A scope's namespace is a Hindsight
/// memory bank. Retain is asynchronous on the server; invalidation is Hindsight's reversible curation state.
/// </summary>
public sealed class HindsightMemory(HttpClient http, string? apiKey = null, int recallMaxTokens = 1024) : IMemoryProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<IReadOnlyList<RecalledMemory>> RecallAsync(string query, MemoryScope scope, CancellationToken ct)
    {
        using var res = await Send(HttpMethod.Post, $"{Bank(scope)}/memories/recall", new { query, budget = "low", max_tokens = recallMaxTokens }, ct);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        return [.. body.GetProperty("results").EnumerateArray().Select(r => new RecalledMemory(r.GetProperty("id").GetString()!, r.GetProperty("text").GetString()!))];
    }

    public async Task RetainAsync(MemoryItem item, MemoryScope scope, CancellationToken ct)
    {
        var entry = new { content = item.Text, item.Context, item.Timestamp, item.DocumentId, item.Tags };
        using var _ = await Send(HttpMethod.Post, $"{Bank(scope)}/memories", new { items = new[] { entry }, @async = true }, ct);
    }

    public async Task InvalidateAsync(string id, string reason, MemoryScope scope, CancellationToken ct)
    {
        using var _ = await Send(HttpMethod.Patch, $"{Bank(scope)}/memories/{Uri.EscapeDataString(id)}", new { state = "invalidated", reason }, ct);
    }

    private static string Bank(MemoryScope scope) => $"v1/default/banks/{Uri.EscapeDataString(scope.Namespace)}";

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: Json) };
        if (apiKey is not null)
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            res.Dispose();
            throw new HttpRequestException($"memory {method} {path}: {(int)res.StatusCode} {text[..Math.Min(200, text.Length)]}", null, res.StatusCode);
        }
        return res;
    }
}
