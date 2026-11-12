using System.Text.Json;
using Chargehand.Config;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Pool.Api;
using Chargehand.Server;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Chargehand.Tests;

/// <summary>The task pool's server side (ADR 0099): the MCP tool "enqueue", the profile key <c>http.pool</c> and the goal a pool worker gets.</summary>
public class PoolServerTests
{
    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static PoolItem Item(string key, string title, string body = "do it", int? pullRequest = null) =>
        new(key, EnqueueTool.Name, "owner/repo", "docs", title, body, 0, pullRequest, DateTimeOffset.UnixEpoch, PoolStatus.Claimed, "w1", null, null, null);

    private static PoolStartRequest Start(ResumeCard? card, params PoolItem[] items) => new(new PoolScope("owner/repo", "docs"), "w1", items, card);

    private static async Task<McpClient> Connect(TestServer s, string bearer) =>
        await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(s.BaseAddress, "/v1/mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearer}" },
            }),
            new McpClientOptions { ProtocolVersion = "2026-07-28", DiscoverProbeTimeout = TimeSpan.FromSeconds(60) });

    private static Task<TestServer> StartWithPool(IReadOnlyList<AccountSettings>? accounts = null) =>
        TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: accounts,
            tweak: (root, settings) => settings with { Pool = new TaskPoolSettings(Database: Path.Combine(root, "pool.db")) });

    private static string Text(CallToolResult call) => string.Join(" | ", call.Content.OfType<TextContentBlock>().Select(c => c.Text));

    [Fact]
    public void Read_takes_the_arguments_and_tags_the_source()
    {
        var request = EnqueueTool.Read(Args("""{"repository":"owner/repo","area":"docs","title":"T","body":"B","priority":7,"pull_request":12}""")) ?? throw new InvalidOperationException("null");

        Assert.Equal(("owner/repo", "docs", "T", "B", 7, (int?)12, "enqueue"),
            (request.Repository, request.Area, request.Title, request.Body, request.Priority, request.PullRequest, request.Source));
    }

    [Fact]
    public void Read_defaults_priority_to_zero_and_allows_json_null()
    {
        var request = EnqueueTool.Read(Args("""{"repository":"owner/repo","area":null,"title":"T","body":"B","pull_request":null}""")) ?? throw new InvalidOperationException("null");

        Assert.Equal((0, (int?)null, (string?)null), (request.Priority, request.PullRequest, request.Area));
    }

    [Theory]
    [InlineData("""{"repository":1,"area":"a","title":"t","body":"b"}""")]
    [InlineData("""{"repository":"o/r","area":["a"],"title":"t","body":"b"}""")]
    [InlineData("""{"repository":"o/r","area":"a","title":true,"body":"b"}""")]
    [InlineData("""{"repository":"o/r","area":"a","title":"t","body":{}}""")]
    [InlineData("""{"repository":"o/r","area":"a","title":"t","body":"b","priority":"1"}""")]
    [InlineData("""{"repository":"o/r","area":"a","title":"t","body":"b","pull_request":"12"}""")]
    public void Read_refuses_an_argument_of_the_wrong_kind(string json) => Assert.Null(EnqueueTool.Read(Args(json)));

    [Theory]
    [InlineData("""{"priority":3000000000}""")]
    [InlineData("""{"priority":1.5}""")]
    [InlineData("""{"pull_request":-3000000000}""")]
    public void Read_turns_a_number_outside_int32_into_minus_one_so_the_pool_refuses_it(string json)
    {
        var request = EnqueueTool.Read(Args(json)) ?? throw new InvalidOperationException("null");

        Assert.Contains(-1, new[] { request.Priority, request.PullRequest ?? 0 });
        Assert.NotNull(request with { Repository = "owner/repo", Area = "docs", Title = "T", Body = "B" } is { } r ? r.Validate() : null);
    }

    [Fact]
    public void One_item_is_its_own_title()
    {
        Assert.Equal("Fix the README", PoolRunStarter.Title(Start(null, Item("k1", "Fix the README"))));
    }

    [Fact]
    public void Several_items_title_by_area_first_item_and_count()
    {
        Assert.Equal("docs: First (+2)", PoolRunStarter.Title(Start(null, Item("k1", "First"), Item("k2", "Second"), Item("k3", "Third"))));
    }

    [Fact]
    public void A_title_is_one_line_cut_to_72_characters()
    {
        var title = PoolRunStarter.Title(Start(null, Item("k1", "line one\nline two " + new string('x', 100))));

        Assert.Equal(72, title.Length);
        Assert.Equal("line one line two " + new string('x', 53) + "…", title);
    }

    [Fact]
    public void The_goal_lists_items_in_order_with_their_pull_request_and_the_card()
    {
        var goal = PoolRunStarter.Goal(Start(new ResumeCard("wrote intro", "write usage", "token ceiling"),
            Item("k1", "Intro", "Write the intro."), Item("k2", "Usage", "Write usage.", pullRequest: 41)));

        Assert.Equal(
            "Work through these items of the area 'docs' in order, in one branch. Each item is a task from the owner's queue.\n"
            + "\nAn earlier worker on this area stopped at its token ceiling. Its card:\n"
            + "- Done: wrote intro\n- Left: write usage\n- Stopped: token ceiling\n"
            + "\n## 1. Intro\nWrite the intro.\n"
            + "\n## 2. Usage\nPull request #41\nWrite usage.\n",
            goal);
    }

    [Fact]
    public void The_profile_reads_http_pool_in_snake_case()
    {
        using var dir = new TempDir();
        var profile = Profile.Load(dir.Write("p.json",
            """{"schema":"profile/v1","http":{"api_key_secret":"k","pool":{"worker_tokens":90000,"max_open_decisions":2,"database":"q.db","max_batch_bytes":4096}}}"""));

        Assert.Equal(new TaskPoolSettings(90_000, 2, "q.db", 4096), profile.Http!.Pool);
    }

    [Fact]
    public async Task With_the_pool_off_enqueue_is_not_listed()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        await using var client = await Connect(s, TestServer.Key);

        Assert.DoesNotContain(EnqueueTool.Name, (await client.ListToolsAsync()).Select(t => t.Name));
    }

    [Fact]
    public async Task The_owner_enqueues_and_gets_a_key_and_position()
    {
        await using var s = await StartWithPool();
        await using var client = await Connect(s, TestServer.Key);
        Assert.Contains(EnqueueTool.Name, (await client.ListToolsAsync()).Select(t => t.Name));

        var call = await client.CallToolAsync(EnqueueTool.Name, new Dictionary<string, object?> { ["repository"] = "owner/repo", ["area"] = "docs", ["title"] = "Fix it", ["body"] = "Fix the typo." });

        Assert.True(call.IsError != true, Text(call));
        var reply = JsonDocument.Parse(Text(call)).RootElement;
        Assert.Equal(("owner/repo", "docs", 1), (reply.GetProperty("repository").GetString(), reply.GetProperty("area").GetString(), reply.GetProperty("position").GetInt32()));
        Assert.False(string.IsNullOrEmpty(reply.GetProperty("key").GetString()));
    }

    [Fact]
    public async Task An_account_key_may_not_enqueue()
    {
        await using var s = await StartWithPool(accounts: [new("bare")]);
        await using var client = await Connect(s, s.Keys.Create("bare", null).Key);

        var call = await client.CallToolAsync(EnqueueTool.Name, new Dictionary<string, object?> { ["repository"] = "owner/repo", ["area"] = "docs", ["title"] = "T", ["body"] = "B" });

        Assert.True(call.IsError);
        Assert.StartsWith("forbidden:", Text(call), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_argument_of_the_wrong_kind_is_invalid()
    {
        await using var s = await StartWithPool();
        await using var client = await Connect(s, TestServer.Key);

        var call = await client.CallToolAsync(EnqueueTool.Name, new Dictionary<string, object?> { ["repository"] = "owner/repo", ["area"] = "docs", ["title"] = "T", ["body"] = "B", ["priority"] = "high" });

        Assert.True(call.IsError);
        Assert.StartsWith("invalid:", Text(call), StringComparison.Ordinal);
    }
}
