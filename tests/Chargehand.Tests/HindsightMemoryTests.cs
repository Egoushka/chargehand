using System.Net;
using System.Text;
using System.Text.Json;
using Chargehand.Memory;

namespace Chargehand.Tests;

public class HindsightMemoryTests
{
    private sealed class Recorder(string response) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, JsonElement Body, string? Auth)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.Method, request.RequestUri!.PathAndQuery, JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone(),
                request.Headers.TryGetValues("Authorization", out var a) ? a.Single() : null));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly MemoryScope Scope = new("hindsight", "orch bank");

    private static (HindsightMemory Memory, Recorder Handler) Make(string response = "{}", string? key = null)
    {
        var h = new Recorder(response);
        return (new HindsightMemory(new HttpClient(h) { BaseAddress = new Uri("http://memory.test:8888/") }, key, 512), h);
    }

    [Fact]
    public async Task Recall_posts_the_query_to_the_namespace_bank_and_maps_results()
    {
        var (m, h) = Make("""{"results":[{"id":"f1","text":"Deploys go through GitOps.","type":"world"}]}""", key: "k");
        var items = await m.RecallAsync("how are deploys done", Scope, CancellationToken.None);
        Assert.Equal([new RecalledMemory("f1", "Deploys go through GitOps.")], items);
        var (method, path, body, auth) = h.Seen.Single();
        Assert.Equal((HttpMethod.Post, "/v1/default/banks/orch%20bank/memories/recall", "Bearer k"), (method, path, auth));
        Assert.Equal("how are deploys done", body.GetProperty("query").GetString());
        Assert.Equal(512, body.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Retain_sends_one_async_item_with_snake_case_fields()
    {
        var (m, h) = Make();
        await m.RetainAsync(new MemoryItem("fact", "ctx", null, "run-1", ["chargehand"]), Scope, CancellationToken.None);
        var body = h.Seen.Single().Body;
        Assert.True(body.GetProperty("async").GetBoolean());
        var item = body.GetProperty("items")[0];
        Assert.Equal("fact", item.GetProperty("content").GetString());
        Assert.Equal("run-1", item.GetProperty("document_id").GetString());
        Assert.False(item.TryGetProperty("timestamp", out _));
    }

    [Fact]
    public async Task Invalidate_uses_the_reversible_curation_state()
    {
        var (m, h) = Make();
        await m.InvalidateAsync("f1", "wrong", Scope, CancellationToken.None);
        var (method, path, body, auth) = h.Seen.Single();
        Assert.Equal((HttpMethod.Patch, "/v1/default/banks/orch%20bank/memories/f1", (string?)null), (method, path, auth));
        Assert.Equal("invalidated", body.GetProperty("state").GetString());
    }
}
