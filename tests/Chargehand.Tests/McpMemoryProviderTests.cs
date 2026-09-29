using System.Net;
using System.Text;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Mcp;
using Chargehand.Memory;
using ModelContextProtocol.Protocol;
using static Chargehand.Tests.MemoryConfigTests;

namespace Chargehand.Tests;

public class McpMemoryProviderTests
{
    private static readonly MemoryScope Scope = new("hindsight", "chargehand");

    private static readonly MemoryScope ChronicleScope = new("chronicle", "chronicle");

    private static MemoryProviderSettings Settings(string json) => JsonSerializer.Deserialize<MemoryProviderSettings>(json, Profile.Json)!;

    private static McpConnectionPool PoolFor(FakeMcpServer server, string name = "gw") =>
        new(new Dictionary<string, McpServerSettings> { [name] = new(Url: "https://mcp.example.internal/mcp") }, _ => "", async (_, _, _) => await server.TransportAsync());

    private const string Found = """{"results":[{"id":"f1","text":"Deploys go through GitOps.","fact_type":"world"}]}""";

    [Fact]
    public async Task Recall_calls_the_mapped_tool_with_templated_arguments_and_reads_the_results()
    {
        await using var server = new FakeMcpServer(new FakeTool("recall", _ => FakeMcpServer.Text(Found)));
        await using var pool = PoolFor(server);

        var facts = await new McpMemoryProvider(Settings(Hindsight), pool).RecallAsync("how are deploys done", Scope, CancellationToken.None);

        Assert.Equal([new RecalledMemory("f1", "Deploys go through GitOps.")], facts);
        var (tool, args) = Assert.Single(server.Calls);
        Assert.Equal("recall", tool);
        Assert.Equal("how are deploys done", args["query"].GetString());
        Assert.Equal("chargehand", args["bank_id"].GetString());
        Assert.Equal("low", args["budget"].GetString());
        Assert.Equal(1024, args["max_tokens"].GetInt32());
    }

    [Fact]
    public async Task The_namespace_is_the_scopes()
    {
        await using var server = new FakeMcpServer(new FakeTool("recall", _ => FakeMcpServer.Text(Found)));
        await using var pool = PoolFor(server);

        await new McpMemoryProvider(Settings(Hindsight), pool).RecallAsync("q", new MemoryScope("hindsight", "other-bank"), CancellationToken.None);

        Assert.Equal("other-bank", Assert.Single(server.Calls).Arguments["bank_id"].GetString());
    }

    [Fact]
    public async Task The_results_mapping_names_the_array_and_the_fields()
    {
        var entry = """{"name":"notes","server":"gw","namespace":"n","tools":{"recall":{"tool":"search_notes","arguments":{"q":"{query}","limit":10},"results":{"path":"notes","id":"key","text":"body"}}}}""";
        await using var server = new FakeMcpServer(new FakeTool("search_notes", _ => FakeMcpServer.Text("""{"notes":[{"key":"k1","body":"The API uses MediatR."}]}""")));
        await using var pool = PoolFor(server);

        var facts = await new McpMemoryProvider(Settings(entry), pool).RecallAsync("api", Scope, CancellationToken.None);

        Assert.Equal([new RecalledMemory("k1", "The API uses MediatR.")], facts);
        Assert.Equal(10, Assert.Single(server.Calls).Arguments["limit"].GetInt32());
    }

    [Fact]
    public async Task A_text_format_takes_the_whole_block_as_one_fact_with_a_hash_id()
    {
        var entry = """{"name":"wiki","server":"gw","namespace":"n","tools":{"recall":{"tool":"ask","arguments":{"q":"{query}"},"results":{"format":"text"}}}}""";
        await using var server = new FakeMcpServer(new FakeTool("ask", _ => FakeMcpServer.Text("Deploys go through GitOps.")));
        await using var pool = PoolFor(server);

        var fact = Assert.Single(await new McpMemoryProvider(Settings(entry), pool).RecallAsync("deploys", Scope, CancellationToken.None));

        Assert.Equal("Deploys go through GitOps.", fact.Text);
        Assert.Equal(12, fact.Id.Length);
    }

    [Fact]
    public async Task A_missing_id_becomes_a_hash_of_the_text()
    {
        await using var server = new FakeMcpServer(new FakeTool("recall", _ => FakeMcpServer.Text("""{"results":[{"text":"no id here"}]}""")));
        await using var pool = PoolFor(server);

        var fact = Assert.Single(await new McpMemoryProvider(Settings(Hindsight), pool).RecallAsync("q", Scope, CancellationToken.None));

        Assert.Equal(12, fact.Id.Length);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("not json")]
    [InlineData("""{"other":[]}""")]
    public async Task A_tool_error_or_a_result_the_mapping_cannot_read_is_a_failure(string reply)
    {
        await using var server = new FakeMcpServer(new FakeTool("recall", _ => reply == "error" ? FakeMcpServer.Error("boom, token: s3cret") : FakeMcpServer.Text(reply)));
        await using var pool = PoolFor(server);

        var e = await Assert.ThrowsAsync<McpMemoryException>(() => new McpMemoryProvider(Settings(Hindsight), pool).RecallAsync("q", Scope, CancellationToken.None));

        Assert.DoesNotContain("s3cret", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_the_pool_cannot_open_is_the_pools_failure()
    {
        await using var pool = new McpConnectionPool(new Dictionary<string, McpServerSettings>(), _ => "");

        var e = await Assert.ThrowsAsync<McpUnavailableException>(() => new McpMemoryProvider(Settings(Hindsight), pool).RecallAsync("q", Scope, CancellationToken.None));

        Assert.Equal(McpUnavailableException.UnknownServer, e.Code);
    }

    [Fact]
    public async Task The_callers_cancellation_reaches_the_call()
    {
        await using var server = new FakeMcpServer(new FakeTool("recall", _ => FakeMcpServer.Text(Found)));
        await using var pool = PoolFor(server);
        var provider = new McpMemoryProvider(Settings(Hindsight), pool);
        await provider.RecallAsync("connect first", Scope, CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.RecallAsync("q", Scope, cancelled.Token));
    }

    [Fact]
    public async Task Retain_omits_null_arguments_and_keeps_tags_an_array()
    {
        await using var server = new FakeMcpServer(new FakeTool("retain", _ => FakeMcpServer.Text("queued")));
        await using var pool = PoolFor(server);

        await new McpMemoryProvider(Settings(Hindsight), pool).RetainAsync(new MemoryItem("fact", "ctx", null, "run-1", ["chargehand", "x"]), Scope, CancellationToken.None);

        var args = Assert.Single(server.Calls).Arguments;
        Assert.Equal("fact", args["content"].GetString());
        Assert.Equal("run-1", args["document_id"].GetString());
        Assert.Equal("chargehand", args["bank_id"].GetString());
        Assert.False(args.ContainsKey("timestamp"));
        Assert.Equal(["chargehand", "x"], args["tags"].EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task A_retain_the_server_rejects_is_a_failure()
    {
        await using var server = new FakeMcpServer(new FakeTool("retain", _ => FakeMcpServer.Error("bank is read-only")));
        await using var pool = PoolFor(server);

        var e = await Assert.ThrowsAsync<McpMemoryException>(() => new McpMemoryProvider(Settings(Hindsight), pool).RetainAsync(new MemoryItem("fact"), Scope, CancellationToken.None));

        Assert.Contains("bank is read-only", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalidate_sends_the_id_and_the_reason()
    {
        await using var server = new FakeMcpServer(new FakeTool("invalidate_memory", _ => FakeMcpServer.Text("ok")));
        await using var pool = PoolFor(server);

        await new McpMemoryProvider(Settings(Hindsight), pool).InvalidateAsync("f1", "wrong", Scope, CancellationToken.None);

        var args = Assert.Single(server.Calls).Arguments;
        Assert.Equal(("f1", "wrong", "chargehand"), (args["memory_id"].GetString(), args["reason"].GetString(), args["bank_id"].GetString()));
    }

    /// <summary>The body of Chronicle's /recall (chronicle/api.py, results built at lines 168-176).</summary>
    private const string ChronicleReply = """
        {"intent":"open","routed_because":null,"window_from_query":null,"results":[
          {"segment_id":"seg-1","score":0.031,"date":"2024-05-03T10:12:00","thread":"chat-1","text":"raw conversation one","evidence":["ev-1","ev-2"],"summary":"Agreed to repaint the flat in June."},
          {"segment_id":"seg-2","score":0.020,"date":"2023-11-20T18:40:00","thread":"chat-2","text":"raw conversation two","evidence":["ev-3"],"summary":null},
          {"segment_id":"seg-3","score":0.010,"date":"2023-01-02T09:00:00","thread":"chat-3","text":"","evidence":[],"summary":null}]}
        """;

    /// <summary>What Chronicle's MCP tool answers (chronicle/mcp_server.py: <c>recall(...) -> str</c> returns the API's text). The
    /// Python SDK puts that text in one text block and, because the return type is str, wraps it as <c>{"result": "…"}</c> in
    /// structuredContent as well.</summary>
    private static CallToolResult ChronicleAnswer() => new()
    {
        Content = [new TextContentBlock { Text = ChronicleReply }],
        StructuredContent = JsonSerializer.SerializeToElement(new { result = ChronicleReply }),
    };

    [Fact]
    public async Task A_chronicle_shaped_recall_reads_the_summary_falls_back_to_the_text_and_skips_an_empty_segment()
    {
        await using var server = new FakeMcpServer(new FakeTool("recall", _ => ChronicleAnswer()));
        await using var pool = PoolFor(server, "chronicle");

        var facts = await new McpMemoryProvider(Settings(Chronicle), pool).RecallAsync("what did we decide about the flat", ChronicleScope, CancellationToken.None);

        Assert.Equal([new RecalledMemory("seg-1", "2024-05-03T10:12:00: Agreed to repaint the flat in June."),
                      new RecalledMemory("seg-2", "2023-11-20T18:40:00: raw conversation two")], facts);
        var (tool, args) = Assert.Single(server.Calls);
        Assert.Equal("recall", tool);
        Assert.Equal(["limit", "query"], args.Keys.Order());   // date_from, date_to and source are left to Chronicle's own routing
        Assert.Equal(("what did we decide about the flat", 10), (args["query"].GetString(), args["limit"].GetInt32()));
    }

    [Fact]
    public async Task The_limit_is_the_entrys_fact_cap()
    {
        var entry = Chronicle.Replace("\"name\":\"chronicle\"", "\"name\":\"chronicle\",\"max_facts\":5", StringComparison.Ordinal);
        await using var server = new FakeMcpServer(new FakeTool("recall", _ => ChronicleAnswer()));
        await using var pool = PoolFor(server, "chronicle");

        await new McpMemoryProvider(Settings(entry), pool).RecallAsync("q", ChronicleScope, CancellationToken.None);

        Assert.Equal(5, Assert.Single(server.Calls).Arguments["limit"].GetInt32());
    }

    [Fact]
    public async Task A_recall_only_provider_refuses_to_retain_or_invalidate_and_calls_nothing()
    {
        await using var server = new FakeMcpServer(new FakeTool("recall", _ => ChronicleAnswer()));
        await using var pool = PoolFor(server, "chronicle");
        var provider = new McpMemoryProvider(Settings(Chronicle), pool);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RetainAsync(new MemoryItem("fact"), ChronicleScope, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.InvalidateAsync("seg-1", "wrong", ChronicleScope, CancellationToken.None));

        Assert.Empty(server.Calls);
    }

    private sealed class Recorder(string recallReply) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, JsonElement Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.Method, request.RequestUri!.AbsolutePath, JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone()));
            var body = request.RequestUri.AbsolutePath.EndsWith("/recall", StringComparison.Ordinal) ? recallReply : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>The bar for deleting HindsightMemory (spec, decision 14): the mapping sends what the HTTP client sends.</summary>
    [Fact]
    public async Task Hindsight_mapping_sends_what_HindsightMemory_sends()
    {
        var item = new MemoryItem("fact", "ctx", new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero), "run-1", ["chargehand", "x"]);
        var http = new Recorder(Found);
        var hindsight = new HindsightMemory(new HttpClient(http) { BaseAddress = new Uri("http://memory.example.internal:8888/") }, apiKey: null, recallMaxTokens: 1024);
        var overHttp = await hindsight.RecallAsync("how are deploys done", Scope, CancellationToken.None);
        await hindsight.RetainAsync(item, Scope, CancellationToken.None);
        await hindsight.InvalidateAsync("f1", "wrong", Scope, CancellationToken.None);

        await using var server = new FakeMcpServer(new FakeTool("recall", _ => FakeMcpServer.Text(Found)), new FakeTool("retain", _ => FakeMcpServer.Text("queued")),
            new FakeTool("invalidate_memory", _ => FakeMcpServer.Text("ok")));
        await using var pool = PoolFor(server);
        var mcp = new McpMemoryProvider(Settings(Hindsight), pool);
        var overMcp = await mcp.RecallAsync("how are deploys done", Scope, CancellationToken.None);
        await mcp.RetainAsync(item, Scope, CancellationToken.None);
        await mcp.InvalidateAsync("f1", "wrong", Scope, CancellationToken.None);

        Assert.Equal(overHttp, overMcp);
        var (recall, retain, invalidate) = (server.Calls[0].Arguments, server.Calls[1].Arguments, server.Calls[2].Arguments);
        Assert.Contains("/banks/chargehand/", http.Seen[0].Path, StringComparison.Ordinal);
        Assert.Equal("chargehand", recall["bank_id"].GetString());
        foreach (var field in new[] { "query", "budget" })
            Assert.Equal(http.Seen[0].Body.GetProperty(field).GetString(), recall[field].GetString());
        Assert.Equal(http.Seen[0].Body.GetProperty("max_tokens").GetInt32(), recall["max_tokens"].GetInt32());

        var sent = http.Seen[1].Body.GetProperty("items")[0];
        Assert.True(http.Seen[1].Body.GetProperty("async").GetBoolean()); // the MCP tool is the asynchronous one, not sync_retain
        foreach (var field in new[] { "content", "context", "document_id", "timestamp" })
            Assert.Equal(sent.GetProperty(field).GetString(), retain[field].GetString());
        Assert.Equal(sent.GetProperty("tags").EnumerateArray().Select(t => t.GetString()), retain["tags"].EnumerateArray().Select(t => t.GetString()));

        Assert.EndsWith("/memories/f1", http.Seen[2].Path, StringComparison.Ordinal);
        Assert.Equal(http.Seen[2].Body.GetProperty("reason").GetString(), invalidate["reason"].GetString());
        Assert.Equal("f1", invalidate["memory_id"].GetString());
    }
}
