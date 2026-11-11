using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chargehand.Contracts;

namespace Chargehand.Modules.Research.Api;

/// <summary>
/// Syncs registry blocks to Langfuse prompt management (ADR 0007): one version per content hash, labelled
/// "sha-&lt;12 hex&gt;". Git stays the source of truth; Langfuse is a mirror for linking spans to prompts.
/// </summary>
public sealed class LangfusePrompts : IDisposable
{
    private readonly HttpClient _http;

    public LangfusePrompts(Uri baseUrl, string publicKey, string secretKey)
    {
        _http = new HttpClient { BaseAddress = baseUrl };
        _http.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{publicKey}:{secretKey}")));
    }

    public void Dispose() => _http.Dispose();

    public static string Label(string sha256) => $"sha-{sha256[..12]}";

    /// <summary>The Langfuse version holding this hash, or null.</summary>
    public async Task<int?> FindAsync(PromptBlock block, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"api/public/v2/prompts/{Uri.EscapeDataString(block.Name)}?label={Label(block.Sha256)}", ct);
        if (!res.IsSuccessStatusCode)
            return null;
        return await VersionOf(res, ct);
    }

    public async Task<int> SyncAsync(PromptBlock block, CancellationToken ct)
    {
        if (await FindAsync(block, ct) is { } existing)
            return existing;
        var body = new JsonObject
        {
            ["name"] = block.Name,
            ["type"] = "text",
            ["prompt"] = block.Text,
            ["labels"] = new JsonArray(Label(block.Sha256), $"v{block.Version}"),
            ["config"] = new JsonObject { ["version"] = block.Version, ["sha256"] = block.Sha256 },
            ["commitMessage"] = $"chargehand {block.Name} {block.Version}",
        };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await _http.PostAsync("api/public/v2/prompts", content, ct);
        res.EnsureSuccessStatusCode();
        return await VersionOf(res, ct);
    }

    private static async Task<int> VersionOf(HttpResponseMessage res, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("version").GetInt32();
    }
}
