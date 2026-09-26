using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;

namespace Chargehand.Evals;

/// <summary>
/// Eval items, dataset runs and scores in Langfuse (ADR 0019): the items live in datasets of the orchestrator's own
/// project, because real tasks are private and never enter the repository. Plain public API; Langfuse's experiment
/// runner exists only in its Python and JS SDKs.
/// </summary>
public sealed class LangfuseEvals
{
    private readonly HttpClient _http;

    public LangfuseEvals(HttpClient http, string publicKey, string secretKey)
    {
        _http = http;
        _http.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{publicKey}:{secretKey}")));
    }

    public async Task<IReadOnlyList<EvalItem>> ItemsAsync(string dataset, CancellationToken ct)
    {
        var items = new List<EvalItem>();
        for (var page = 1; ; page++)
        {
            var body = await Get($"api/public/dataset-items?datasetName={Uri.EscapeDataString(dataset)}&page={page}&limit=50", ct);
            foreach (var d in body.GetProperty("data").EnumerateArray().Where(d => d.GetProperty("status").GetString() == "ACTIVE"))
                items.Add(new EvalItem(d.GetProperty("id").GetString()!, d.GetProperty("input").Deserialize<RunRequest>(ContractJson.Options)!,
                    d.GetProperty("expectedOutput").Deserialize<EvalExpected>(ContractJson.Options)!,
                    d.GetProperty("metadata").ValueKind == JsonValueKind.Object ? d.GetProperty("metadata").Deserialize<Dictionary<string, string>>() : null));
            if (page >= body.GetProperty("meta").GetProperty("totalPages").GetInt32())
                return items.OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Creates the dataset if it is missing, then upserts each item by id.</summary>
    public async Task PushAsync(string dataset, IReadOnlyList<EvalItem> items, CancellationToken ct)
    {
        using (var found = await _http.GetAsync($"api/public/v2/datasets/{Uri.EscapeDataString(dataset)}", ct))
            if (found.StatusCode == HttpStatusCode.NotFound)
                await Post("api/public/v2/datasets", new { name = dataset, description = "chargehand eval items (ADR 0019)" }, ct);
            else
                found.EnsureSuccessStatusCode();
        foreach (var item in items)
            await Post("api/public/dataset-items", new
            {
                datasetName = dataset,
                id = item.Id,
                input = JsonSerializer.SerializeToElement(item.Request, ContractJson.Options),
                expectedOutput = JsonSerializer.SerializeToElement(item.Expected, ContractJson.Options),
                metadata = item.Metadata,
            }, ct);
    }

    /// <summary>Links a run's trace to its item in a named dataset run.</summary>
    public Task LinkAsync(string runName, string itemId, string traceId, CancellationToken ct) =>
        Post("api/public/dataset-run-items", new { runName, datasetItemId = itemId, traceId }, ct);

    public Task ScoreAsync(string traceId, string name, double value, string comment, CancellationToken ct) =>
        Post("api/public/scores", new { traceId, name, value, dataType = "NUMERIC", comment }, ct);

    private async Task<JsonElement> Get(string path, CancellationToken ct)
    {
        using var res = await _http.GetAsync(path, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private async Task Post(string path, object body, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync(path, body, ct);
        res.EnsureSuccessStatusCode();
    }
}
