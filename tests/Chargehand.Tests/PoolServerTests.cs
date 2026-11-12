using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Pool.Api;
using Chargehand.Modules.Tasks.Api;
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
            tweak: (root, settings) => settings with
            {
                Pool = new TaskPoolSettings(Database: Path.Combine(root, "pool.db")),
                Profile = (settings.Profile ?? Runs.Profile(root)) with
                {
                    Automation = new AutomationSettings(new AutomationGitHub(new Dictionary<string, AutomationRepository> { ["owner/repo"] = new(root) })),
                },
            });

    private static Dictionary<string, object?> Enqueue(string repository = "owner/repo", string title = "T", string body = "B") =>
        new() { ["repository"] = repository, ["area"] = "docs", ["title"] = title, ["body"] = body };

    private static ResultContract Result(ResultStatus status, long tokens, ErrorCode? error = null, string summary = "wrote the intro") =>
        new("result/v1", "run-1", "driven", "trace", new PromptChain([], new AsSent("claude-code/1", "driven", "batch", "2026-10-13")), status, summary, [], [], [], [], 0.5,
            new Usage(tokens, 0, 0, 0, null), error is { } code ? new ResultError(code, "stopped", false) : null);

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
            """{"schema":"profile/v1","http":{"api_key_secret":"k","pool":{"worker_tokens":90000,"max_open_decisions":2,"database":"q.db","max_batch_bytes":4096,"max_queued":7}}}"""));

        Assert.Equal(new TaskPoolSettings(90_000, 2, "q.db", 4096, 7), profile.Http!.Pool);
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
    public async Task The_listed_input_schema_is_the_published_enqueue_v1()
    {
        await using var s = await StartWithPool();
        await using var client = await Connect(s, TestServer.Key);

        var tool = (await client.ListToolsAsync()).Single(t => t.Name == EnqueueTool.Name);

        var published = JsonDocument.Parse(File.ReadAllText(Repo.Path("schemas/enqueue/v1/enqueue.schema.json"))).RootElement;
        Assert.True(JsonElement.DeepEquals(published, tool.ProtocolTool.InputSchema), tool.ProtocolTool.InputSchema.GetRawText());
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

    [Fact]
    public async Task A_sessions_run_token_may_not_enqueue_though_it_acts_for_the_owner()
    {
        await using var s = await StartWithPool();
        var token = s.Tokens.Issue(new RunTokenClaims("run-parent", s.WorkerRoot, "0000000", null, null, DateTimeOffset.UtcNow.AddHours(1)), DateTimeOffset.UtcNow);
        await using var client = await Connect(s, token);

        var call = await client.CallToolAsync(EnqueueTool.Name, Enqueue());

        Assert.True(call.IsError);
        Assert.StartsWith("forbidden:", Text(call), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repository_the_server_does_not_work_on_is_refused_without_quoting_it()
    {
        await using var s = await StartWithPool();
        await using var client = await Connect(s, TestServer.Key);

        var call = await client.CallToolAsync(EnqueueTool.Name, Enqueue(repository: "someone/elsewhere"));

        Assert.True(call.IsError);
        Assert.StartsWith("invalid:", Text(call), StringComparison.Ordinal);
        Assert.DoesNotContain("elsewhere", Text(call), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bad_input_is_refused_through_the_tool_with_stable_codes_and_the_value_is_never_quoted()
    {
        await using var s = await StartWithPool();
        await using var client = await Connect(s, TestServer.Key);
        var secret = "AKIA" + "ABCDEFGHIJKLMNOP";

        var missing = await client.CallToolAsync(EnqueueTool.Name, new Dictionary<string, object?> { ["repository"] = "owner/repo", ["area"] = "docs", ["body"] = "B" });
        var large = await client.CallToolAsync(EnqueueTool.Name, Enqueue(body: new string('b', EnqueueRequest.MaxBodyBytes + 1)));
        var leaked = await client.CallToolAsync(EnqueueTool.Name, Enqueue(body: "use " + secret));

        Assert.Equal(("invalid:", "too_large:", "secret:"),
            (Text(missing)[..8], Text(large)[..10], Text(leaked)[..7]));
        Assert.True(missing.IsError == true && large.IsError == true && leaked.IsError == true);
        Assert.DoesNotContain(secret, Text(leaked), StringComparison.Ordinal);
    }

    /// <summary>A batch result as the batch module builds it: one task, the worker, with the given end.</summary>
    private static ResultContract Batch(TaskState state, ErrorCode? error, long tokens, string? detail = "stopped: the task's cap was exceeded", string? branch = "pool/docs",
        bool cancelled = false) =>
        DrivenResult.BuildBatch("run-1", new string('0', 32), "Pool", new BatchOutcome([new TaskOutcome("w-1", state, "run-t", detail, error, Tokens: tokens, Branch: branch)], null),
            "env", priced: false, "2.1.283", cancelledByCaller: cancelled);

    [Fact]
    public void A_batch_whose_worker_stopped_at_the_token_ceiling_leaves_a_card_for_its_successor()
    {
        var card = PoolRunStarter.Card(Batch(TaskState.Failed, ErrorCode.CostCapReached, 150_000), "w-1", 150_000);

        Assert.Equal(new ResumeCard("Branch pool/docs. stopped: the task's cap was exceeded", "Every item below; check each against the repository before redoing it.",
            "The worker's token ceiling of 150000 tokens."), card);
    }

    [Fact]
    public void A_cards_done_is_cut()
    {
        var card = PoolRunStarter.Card(Batch(TaskState.Failed, ErrorCode.CostCapReached, 10, detail: new string('s', 5000), branch: null), "w-1", 10)!;

        Assert.Equal(new string('s', PoolRunStarter.MaxCardDone - 1) + "…", card.Done);
    }

    [Theory]
    [InlineData(TaskState.Completed, null, 10, "w-1", false)]
    [InlineData(TaskState.Failed, ErrorCode.SessionFailed, 10, "w-1", false)]
    [InlineData(TaskState.Failed, ErrorCode.CostCapReached, 0, "w-1", false)]
    [InlineData(TaskState.Failed, ErrorCode.CostCapReached, 10, "w-other", false)]
    [InlineData(TaskState.Failed, ErrorCode.CostCapReached, 10, "w-1", true)]
    public void Any_other_end_a_stop_with_no_progress_another_workers_row_or_a_cancel_leaves_no_card(TaskState state, ErrorCode? error, long tokens, string worker, bool cancelled) =>
        Assert.Null(PoolRunStarter.Card(Batch(state, error, tokens, cancelled: cancelled), worker, 10));

    [Fact]
    public void A_result_with_no_batch_artifact_leaves_no_card() =>
        Assert.Null(PoolRunStarter.Card(Result(ResultStatus.Failed, 10, ErrorCode.CostCapReached), "w-1", 10));

    [Fact]
    public void Hand_offs_count_the_ceiling_stops_just_before_the_worker_in_its_area()
    {
        var docs = new PoolScope("owner/repo", "docs");
        var t0 = DateTimeOffset.UnixEpoch;
        PoolWorker W(string id, int minute, string? reason, PoolScope? scope = null, bool released = true) =>
            new(id, scope ?? docs, 0, t0.AddMinutes(minute), released ? t0.AddMinutes(minute + 1) : null, reason);
        var self = W("w-5", 5, null, released: false);
        PoolWorker[] workers = [W("w-1", 1, "ceiling"), W("w-2", 2, "done"), W("w-3", 3, "ceiling"), W("w-x", 3, "ceiling", new PoolScope("owner/repo", "pool")), W("w-4", 4, "ceiling"), self];

        Assert.Equal(2, PoolRunStarter.HandOffs(workers, self));
    }

    [Fact]
    public void After_a_restart_a_worker_whose_batch_was_resumed_stays_and_the_rest_do_not()
    {
        var scope = new PoolScope("owner/repo", "docs");
        PoolWorker Worker(string id, DateTimeOffset? released = null) => new(id, scope, 0, DateTimeOffset.UnixEpoch, released, released is null ? null : "done");
        using var resumed = new RunHandle("run-1", CancellationToken.None) { IsBatch = true, ResumedTasks = ["w-live", "w-released"] };
        using var fresh = new RunHandle("run-2", CancellationToken.None);

        var found = PoolRunStarter.Resumed([resumed, fresh], [Worker("w-live"), Worker("w-gone"), Worker("w-released", DateTimeOffset.UnixEpoch)]);

        Assert.Equal(("run-1", "w-live"), Assert.Single(found.Select(f => (f.RunId, f.Worker.Id))));
    }
}
