using Chargehand.Config;
using Chargehand.Mcp;
using static Chargehand.Tests.MemoryConfigTests;

namespace Chargehand.Tests;

/// <summary>Goal 0.6: setup mistakes show up before a run does.</summary>
public class ExtensionsCheckTests
{
    private const string Recall = """{"type":"object","properties":{"query":{"type":"string"},"bank_id":{"type":"string"},"budget":{"type":"string"},"max_tokens":{"type":"number"}}}""";
    private const string Retain = """{"type":"object","properties":{"content":{"type":"string"},"context":{"type":"string"},"document_id":{"type":"string"},"timestamp":{"type":"string"},"tags":{"type":"array"},"bank_id":{"type":"string"}}}""";
    private const string Invalidate = """{"type":"object","properties":{"memory_id":{"type":"string"},"reason":{"type":"string"},"bank_id":{"type":"string"}}}""";

    private static Profile ProfileWith(string memory, string servers = """{"gw":{"url":"https://mcp.example.internal/mcp"}}""")
    {
        using var dir = new TempDir();
        return Profile.Load(dir.Write("p.json", $$"""{"schema":"profile/v1","mcp_servers":{{servers}},"memory":{{memory}}}"""));
    }

    private static FakeMcpServer HindsightServer(bool withRetain = true) => new(
        new FakeTool("recall", _ => FakeMcpServer.Text("""{"results":[{"id":"f1","text":"Deploys go through GitOps."}]}"""), Schema: Recall),
        withRetain ? new FakeTool("retain", _ => FakeMcpServer.Text("queued"), Schema: Retain) : new FakeTool("other", _ => FakeMcpServer.Text("x")),
        new FakeTool("invalidate_memory", _ => FakeMcpServer.Text("ok"), Schema: Invalidate));

    private static McpConnectionPool PoolFor(Profile profile, FakeMcpServer server) =>
        new(profile.McpServers!, _ => "", async (_, _, _) => await server.TransportAsync());

    [Fact]
    public async Task A_valid_setup_is_ok_and_lists_each_server_and_mapping()
    {
        var profile = ProfileWith($"[{Hindsight}]");
        await using var server = HindsightServer();
        await using var pool = PoolFor(profile, server);

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal([
            "server gw: connected, 3 tools",
            "memory hindsight: recall -> recall ok",
            "memory hindsight: retain -> retain ok",
            "memory hindsight: invalidate -> invalidate_memory ok"], result.Lines);
    }

    [Fact]
    public async Task A_mapped_tool_the_server_does_not_list_is_a_problem_with_an_action()
    {
        var profile = ProfileWith($"[{Hindsight}]");
        await using var server = HindsightServer(withRetain: false);
        await using var pool = PoolFor(profile, server);

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, null, CancellationToken.None);

        Assert.False(result.Ok);
        var line = Assert.Single(result.Lines, l => l.Contains("retain ->", StringComparison.Ordinal) && !l.EndsWith(" ok", StringComparison.Ordinal));
        Assert.StartsWith("memory hindsight: retain -> tool 'retain' is not listed by server 'gw'; action: ", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_argument_the_tools_schema_lacks_is_a_problem()
    {
        var entry = """{"name":"hs","server":"gw","namespace":"n","tools":{"recall":{"tool":"recall","arguments":{"q":"{query}"}}}}""";
        var profile = ProfileWith($"[{entry}]");
        await using var server = HindsightServer();
        await using var pool = PoolFor(profile, server);

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(result.Lines, l => l.StartsWith("memory hs: recall -> argument 'q' is not a property of recall (properties: query, bank_id, budget, max_tokens); action: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_required_argument_the_mapping_leaves_out_is_a_problem()
    {
        var entry = """{"name":"hs","server":"gw","namespace":"n","tools":{"recall":{"tool":"recall","arguments":{"query":"{query}"}}}}""";
        var profile = ProfileWith($"[{entry}]");
        await using var server = new FakeMcpServer(new FakeTool("recall", _ => FakeMcpServer.Text("x"),
            Schema: """{"type":"object","properties":{"query":{"type":"string"},"bank_id":{"type":"string"}},"required":["query","bank_id"]}"""));
        await using var pool = PoolFor(profile, server);

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(result.Lines, l => l.StartsWith("memory hs: recall -> required argument 'bank_id' of recall is not in the mapping; action: ", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Lines, l => l.Contains("'query'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_schema_that_allows_other_properties_accepts_any_argument()
    {
        var entry = """{"name":"hs","server":"gw","namespace":"n","tools":{"recall":{"tool":"recall","arguments":{"q":"{query}"}}}}""";
        var profile = ProfileWith($"[{entry}]");
        await using var server = new FakeMcpServer(new FakeTool("recall", _ => FakeMcpServer.Text("x"),
            Schema: """{"type":"object","properties":{"query":{"type":"string"}},"additionalProperties":true}"""));
        await using var pool = PoolFor(profile, server);

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("memory hs: recall -> recall ok", result.Lines);
    }

    [Fact]
    public async Task An_unreachable_server_is_a_problem_and_the_others_are_still_checked()
    {
        var servers = """{"down":{"url":"https://a.example.internal/mcp"},"gw":{"url":"https://mcp.example.internal/mcp"}}""";
        var profile = ProfileWith($"[{Hindsight}]", servers);
        await using var server = HindsightServer();
        await using var pool = new McpConnectionPool(profile.McpServers!, _ => "", async (name, _, _) =>
            name == "down" ? throw new IOException("connection refused") : await server.TransportAsync());

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(result.Lines, l => l.StartsWith("server down: unreachable: connection refused; action: ", StringComparison.Ordinal));
        Assert.Contains("server gw: connected, 3 tools", result.Lines);
        Assert.Contains("memory hindsight: recall -> recall ok", result.Lines);
    }

    [Fact]
    public async Task A_memory_on_an_unreachable_server_says_it_was_not_checked()
    {
        var servers = """{"gw":{"url":"https://mcp.example.internal/mcp"}}""";
        var profile = ProfileWith($"[{Hindsight}]", servers);
        await using var pool = new McpConnectionPool(profile.McpServers!, _ => "", (_, _, _) => throw new IOException("connection refused"));

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(result.Lines, l => l.StartsWith("memory hindsight: server 'gw' is not available, so its tools were not checked; action: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unresolved_secret_names_the_item_and_not_a_value()
    {
        var servers = """{"gw":{"url":"https://mcp.example.internal/mcp","headers":{"Authorization":"Bearer {secret:gw-token}"}}}""";
        var profile = ProfileWith("[]", servers);
        await using var pool = new McpConnectionPool(profile.McpServers!, item => throw new InvalidOperationException($"no secret source resolved '{item}'"));

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(result.Lines, l => l.StartsWith("server gw: secret_unresolved: no secret source resolved 'gw-token'; action: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failure_reason_never_carries_a_url_or_a_credential()
    {
        var profile = ProfileWith("[]");
        await using var pool = new McpConnectionPool(profile.McpServers!, _ => "",
            (_, _, _) => throw new IOException("cannot reach https://user:hunter2@mcp.example.internal/mcp?token=abc123 (Authorization: Bearer sk-live-999)"));

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, null, CancellationToken.None);

        var line = Assert.Single(result.Lines, l => l.StartsWith("server gw: ", StringComparison.Ordinal));
        foreach (var leaked in new[] { "hunter2", "abc123", "sk-live-999", "https://", "mcp.example.internal" })
            Assert.DoesNotContain(leaked, line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_in_time_is_a_problem()
    {
        var profile = ProfileWith("[]");
        await using var server = HindsightServer();
        await using var pool = PoolFor(profile, server);

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, null, CancellationToken.None, timeout: TimeSpan.Zero);

        Assert.False(result.Ok);
        Assert.Contains(result.Lines, l => l.StartsWith("server gw: unreachable: no answer within 0 s; action: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_probe_runs_recall_and_prints_the_count_and_never_a_fact()
    {
        var profile = ProfileWith($"[{Hindsight}]");
        await using var server = HindsightServer();
        await using var pool = PoolFor(profile, server);

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, "deploys", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("memory hindsight: probe returned 1 fact(s)", result.Lines);
        Assert.DoesNotContain(result.Lines, l => l.Contains("GitOps", StringComparison.Ordinal));
        var call = Assert.Single(server.Calls, c => c.Tool == "recall");
        Assert.Equal("deploys", call.Arguments["query"].GetString());
    }

    [Fact]
    public async Task A_probe_the_provider_cannot_read_is_a_problem()
    {
        var profile = ProfileWith($"[{Hindsight}]");
        await using var server = new FakeMcpServer(new FakeTool("recall", _ => FakeMcpServer.Error("bank not found"), Schema: Recall),
            new FakeTool("retain", _ => FakeMcpServer.Text("x"), Schema: Retain), new FakeTool("invalidate_memory", _ => FakeMcpServer.Text("x"), Schema: Invalidate));
        await using var pool = PoolFor(profile, server);

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, "deploys", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(result.Lines, l => l.StartsWith("memory hindsight: probe failed: the tool reported an error: bank not found; action: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_probe_no_recall_is_sent()
    {
        var profile = ProfileWith($"[{Hindsight}]");
        await using var server = HindsightServer();
        await using var pool = PoolFor(profile, server);

        await ExtensionsCheck.RunAsync(profile, [], pool, null, CancellationToken.None);

        Assert.Empty(server.Calls);
    }

    [Fact]
    public async Task A_recall_only_provider_is_checked_for_recall_alone()
    {
        var servers = """{"chronicle":{"url":"http://localhost:8031/sse","transport":"sse"}}""";
        var profile = ProfileWith($"[{Chronicle}]", servers);
        await using var server = new FakeMcpServer(new FakeTool("recall", _ => FakeMcpServer.Text("""{"results":[]}"""),
            Schema: """{"type":"object","properties":{"query":{"type":"string"},"date_from":{},"date_to":{},"source":{},"limit":{"type":"integer"}}}"""));
        await using var pool = new McpConnectionPool(profile.McpServers!, _ => "", async (_, _, _) => await server.TransportAsync());

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("memory chronicle: recall -> recall ok", result.Lines);
        Assert.DoesNotContain(result.Lines, l => l.Contains("retain", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_object_form_is_noted_and_not_checked()
    {
        var profile = ProfileWith("""{"backend":"hindsight","url":"http://memory.example.internal:8888","namespace":"ns"}""", "{}");
        await using var pool = new McpConnectionPool(new Dictionary<string, McpServerSettings>(), _ => "");

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, null, CancellationToken.None);

        Assert.True(result.Ok);
        var line = Assert.Single(result.Lines);
        Assert.StartsWith("memory: the single-object form", line, StringComparison.Ordinal);
        Assert.DoesNotContain("memory.example.internal", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_to_check_is_said_and_is_ok()
    {
        await using var pool = new McpConnectionPool(new Dictionary<string, McpServerSettings>(), _ => "");

        var result = await ExtensionsCheck.RunAsync(new Profile("profile/v1"), [], pool, null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(["nothing to check: the profile lists no mcp_servers, memory or preset services"], result.Lines);
    }

    [Fact]
    public async Task An_entry_the_mapping_rules_refuse_is_reported_and_not_called()
    {
        var entry = new MemoryProviderSettings("hs", "absent", new MemoryTools(new ToolCall("recall", new Dictionary<string, System.Text.Json.JsonElement>())));
        var profile = new Profile("profile/v1", McpServers: new Dictionary<string, McpServerSettings> { ["gw"] = new(Url: "https://mcp.example.internal/mcp") },
            Memory: new MemoryBlock([entry]));
        await using var server = HindsightServer();
        await using var pool = PoolFor(profile, server);

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, "q", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(result.Lines, l => l.StartsWith("memory.hs: server 'absent' is not defined in mcp_servers (defined: gw); action: ", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Lines, l => l.StartsWith("memory hs:", StringComparison.Ordinal));
        Assert.Empty(server.Calls);
    }

    [Fact]
    public async Task A_presets_service_is_checked_against_the_servers_tools()
    {
        var servers = """{"team-docs":{"url":"https://mcp.example.internal/docs"}}""";
        var profile = ProfileWith("[]", servers);
        await using var docs = new FakeMcpServer(new FakeTool("search_docs", _ => FakeMcpServer.Text("x")), new FakeTool("search_wiki", _ => FakeMcpServer.Text("x")));
        await using var pool = new McpConnectionPool(profile.McpServers!, _ => "", async (_, _, _) => await docs.TransportAsync());
        var preset = PresetRoot.Load("docs", "    services:\n      - server: team-docs\n        tools: [search_docs, missing_tool, \"search_*\"]\n");

        var result = await ExtensionsCheck.RunAsync(profile, [preset], pool, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("preset docs: service team-docs: search_docs ok", result.Lines);
        Assert.Contains("preset docs: service team-docs: search_* ok (2 tools)", result.Lines);
        Assert.Contains(result.Lines, l => l.StartsWith("preset docs: service team-docs: tool 'missing_tool' is not listed; action: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_preset_naming_a_server_the_profile_lacks_or_one_that_is_down_is_a_problem()
    {
        var servers = """{"team-docs":{"url":"https://mcp.example.internal/docs"}}""";
        var profile = ProfileWith("[]", servers);
        await using var pool = new McpConnectionPool(profile.McpServers!, _ => "", (_, _, _) => throw new IOException("connection refused"));
        var preset = PresetRoot.Load("docs", "    services:\n      - server: team-docs\n        tools: [search_docs]\n      - server: elsewhere\n        tools: [x]\n");

        var result = await ExtensionsCheck.RunAsync(profile, [preset], pool, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(result.Lines, l => l.StartsWith("preset docs: service team-docs: server is not available, so its tools were not checked; action: ", StringComparison.Ordinal));
        Assert.Contains(result.Lines, l => l.StartsWith("preset docs: service elsewhere: server 'elsewhere' is not defined in mcp_servers (defined: team-docs); action: ", StringComparison.Ordinal));
    }

    [Fact]
    public void Presets_load_from_a_directory_one_or_all_and_a_broken_one_is_a_problem()
    {
        using var root = new PresetRoot("docs", "    services:\n      - server: team-docs\n        tools: [search_docs]\n");
        var directory = Path.Combine(root.Path, "presets");
        File.WriteAllText(Path.Combine(directory, "broken.yaml"), "schema: preset/v1\nname: [unbalanced\n");

        var (all, problems) = ExtensionsCheck.LoadPresets(directory, null);
        var (one, none) = ExtensionsCheck.LoadPresets(directory, "docs");

        Assert.Equal(["docs"], all.Select(p => p.Name));
        var problem = Assert.Single(problems);
        Assert.StartsWith("preset broken: ", problem, StringComparison.Ordinal);
        Assert.Contains("; action: fix presets/broken.yaml", problem, StringComparison.Ordinal);
        Assert.Equal(["docs"], one.Select(p => p.Name));
        Assert.Empty(none);
        Assert.Throws<ChargehandException>(() => ExtensionsCheck.LoadPresets(directory, "nope"));
    }
}
