using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary><c>GET /v1/batches/{id}</c> and <c>/tasks/{task}</c> (CHARGEHAND-209): a batch read from the run log, the learning records, the saved state of an unfinished batch and the events the server holds.</summary>
public class ConsoleBatchTests
{
    private const string Batch = "run-b1";

    private static AccountSettings Alice() => new("alice", Driven: true, AllowServerCredential: true);

    private static AccountSettings Bob() => new("bob", Driven: true, AllowServerCredential: true);

    private static Profile AskProfile(string root) => Runs.Profile(root) with
    {
        Driven = new DrivenSettings(Integrator: new DrivenIntegrator(Repositories: new Dictionary<string, string> { ["o/r"] = "ask" })),
    };

    private static readonly string[] CheckCommand = ["sh", "scripts/check.sh"];

    private static Artifact[] Artifacts() =>
    [
        ConsoleSeed.Inline("branch", new { branch = "chargehand/run-b1-t1", commit = "c0ffee", @base = ConsoleRig.Commit, pushed = true }),
        ConsoleSeed.Inline("pull-request", new { url = "https://example.test/o/r/pull/7", number = 7, draft = true, head = "chargehand/run-b1-t1" }),
        ConsoleSeed.Inline("verification", new { argv = CheckCommand, exit_code = 0, passed = true, output_tail = "ok", test_integrity = new { flagged = true, reason = "only tests changed" } }),
        ConsoleSeed.Inline("session-tests", new { claimed_by = "session", command = "dotnet test", exit_code = 0 }),
        ConsoleSeed.Inline("changes", new { @base = ConsoleRig.Commit, commit = "c0ffee", files_total = 1, files = new[] { new { path = "src/a.cs", added = 3, removed = 1, binary = false } }, diff = "diff --git a/src/a.cs b/src/a.cs", diff_truncated = false }),
        ConsoleSeed.Inline("review", new { runs = new[] { new { run_id = "run-review-1", preset = "review", status = "completed" } } }),
    ];

    private static async Task SeedFinishedAsync(ConsoleRig rig, string? actor = null)
    {
        await ConsoleSeed.BatchAsync(rig, Batch, actor,
            new SeedTask("t1", Goal: "Add a retry", PrUrl: "https://example.test/o/r/pull/7", Branch: "chargehand/run-b1-t1", Artifacts: Artifacts(),
                Asked: new DrivenTask("t1", Goal: "Add a retry", Size: "L", DependsOn: [])),
            new SeedTask("t2", "failed", Goal: "Add a backoff", Error: ErrorCode.SessionStalled, Detail: "stalled", Asked: new DrivenTask("t2", Goal: "Add a backoff", Size: "S", DependsOn: ["t1"])),
            new SeedTask("t3", "not_started", Goal: "Docs", Detail: "not started: the task it depends on, 't2', failed", NoRun: true, Asked: new DrivenTask("t3", Goal: "Docs", DependsOn: ["t2"])));
        var governor = new[] { new GovernorCount("spend", "checkpoint", 1, 1), new GovernorCount("repeated_call", "warn", 2, 0) };
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned(Batch, "t1", actor: actor, size: "L", pr: ConsoleSeed.Pull(7), integrityFlag: true, continuations: 1, governor: governor,
            source: new BudgetSource("history", 22, 80, "repository")));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned(Batch, "t2", LearningOutcome.Failed, actor, size: "S", usd: null));
    }

    [Fact]
    public async Task A_finished_batch_reads_back_its_graph_budget_spend_governor_and_pull_request()
    {
        await using var rig = await ConsoleRig.StartAsync(profile: AskProfile);
        await SeedFinishedAsync(rig);

        var body = await ConsoleRig.Get(rig.Owner, $"/v1/batches/{Batch}");

        Assert.Equal("batch/v1", body.GetProperty("contract_version").GetString());
        var run = body.GetProperty("run");
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.RunSummary, run));
        Assert.Equal(("batch", "failed", "tasks_incomplete"), (run.GetProperty("kind").GetString(), run.GetProperty("status").GetString(), run.GetProperty("error_code").GetString()));
        Assert.Equal(("/srv/repo", ConsoleRig.Commit), (body.GetProperty("repository").GetProperty("path").GetString(), body.GetProperty("repository").GetProperty("commit").GetString()));
        Assert.Equal(2, body.GetProperty("limits").GetProperty("max_parallel").GetInt32());
        Assert.Equal("token_exchange", body.GetProperty("credential_delivery").GetString());
        Assert.Equal("failed", body.GetProperty("result").GetProperty("status").GetString());
        Assert.Contains(body.GetProperty("artifacts").EnumerateArray(), a => a.GetProperty("kind").GetString() == "driven-batch");

        var tasks = body.GetProperty("tasks").EnumerateArray().ToDictionary(t => t.GetProperty("id").GetString()!);
        Assert.Equal(["t1", "t2", "t3"], tasks.Keys);
        var t1 = tasks["t1"];
        Assert.Equal(("completed", "L", "run-b1-t1"), (t1.GetProperty("state").GetString(), t1.GetProperty("size").GetString(), t1.GetProperty("run_id").GetString()));
        Assert.Equal("Add a retry", t1.GetProperty("goal").GetString());
        Assert.Equal("chargehand/run-b1-t1", t1.GetProperty("branch").GetString());
        Assert.Equal("history", t1.GetProperty("budget").GetProperty("source").GetProperty("kind").GetString());
        Assert.Equal(22, t1.GetProperty("budget").GetProperty("source").GetProperty("n").GetInt32());
        Assert.Equal(4m, t1.GetProperty("budget").GetProperty("usd").GetDecimal());
        var spend = t1.GetProperty("spend");
        Assert.Equal((1000, 200, 3000, 400, 1), (spend.GetProperty("input_tokens").GetInt32(), spend.GetProperty("output_tokens").GetInt32(), spend.GetProperty("cache_read_tokens").GetInt32(),
            spend.GetProperty("cache_write_tokens").GetInt32(), spend.GetProperty("continuations").GetInt32()));
        Assert.Equal(1.5m, spend.GetProperty("usd_estimate").GetDecimal());
        Assert.Equal(["spend:checkpoint:1:1", "repeated_call:warn:2:0"],
            t1.GetProperty("governor").GetProperty("decisions").EnumerateArray().OrderByDescending(d => d.GetProperty("signal").GetString() == "spend")
                .Select(d => $"{d.GetProperty("signal").GetString()}:{d.GetProperty("action").GetString()}:{d.GetProperty("count").GetInt32()}:{d.GetProperty("acted").GetInt32()}"));
        Assert.True(t1.GetProperty("test_integrity_flagged").GetBoolean());

        Assert.Equal(["t1"], tasks["t2"].GetProperty("depends_on").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal("failed", tasks["t2"].GetProperty("state").GetString());
        Assert.Equal("session_stalled", tasks["t2"].GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("not_started", tasks["t3"].GetProperty("state").GetString());
        Assert.Equal(["t2"], tasks["t3"].GetProperty("depends_on").EnumerateArray().Select(d => d.GetString()));
        Assert.False(tasks["t3"].TryGetProperty("run_id", out _));

        var pr = Assert.Single(body.GetProperty("pull_requests").EnumerateArray());
        Assert.Equal(("https://example.test/o/r/pull/7", "o/r", "open"), (pr.GetProperty("url").GetString(), pr.GetProperty("repository").GetString(), pr.GetProperty("state").GetString()));
        Assert.Equal(("ask", true, "test_integrity"), (pr.GetProperty("integrator_mode").GetString(), pr.GetProperty("awaiting_person").GetBoolean(), pr.GetProperty("awaiting_reason").GetString()));
        Assert.False(body.GetProperty("timeline").GetProperty("available").GetBoolean());
        Assert.NotEmpty(body.GetProperty("not_recorded").EnumerateArray());
    }

    [Fact]
    public async Task A_task_reads_back_what_the_session_and_chargehand_checked_and_the_diff()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await SeedFinishedAsync(rig);

        var body = await ConsoleRig.Get(rig.Owner, $"/v1/batches/{Batch}/tasks/t1");

        Assert.Equal("batch-task/v1", body.GetProperty("contract_version").GetString());
        Assert.Equal(Batch, body.GetProperty("batch_run_id").GetString());
        Assert.Equal("summary of t1", body.GetProperty("summary").GetString());
        var verification = body.GetProperty("verification");
        Assert.Equal(("sh", 0, true), (verification.GetProperty("argv")[0].GetString(), verification.GetProperty("exit_code").GetInt32(), verification.GetProperty("passed").GetBoolean()));
        Assert.Equal("only tests changed", verification.GetProperty("test_integrity").GetProperty("reason").GetString());
        Assert.Equal("dotnet test", body.GetProperty("session_tests").GetProperty("command").GetString());
        var changes = body.GetProperty("changes");
        Assert.Equal("src/a.cs", changes.GetProperty("files")[0].GetProperty("path").GetString());
        Assert.StartsWith("diff --git", changes.GetProperty("diff").GetString(), StringComparison.Ordinal);
        Assert.Equal("run-review-1", body.GetProperty("review").GetProperty("runs")[0].GetProperty("run_id").GetString());
        Assert.Contains(body.GetProperty("artifacts").EnumerateArray(), a => a.GetProperty("kind").GetString() == "verification");
        Assert.Equal("Add a retry", body.GetProperty("task").GetProperty("goal").GetString());
        Assert.Equal("L", body.GetProperty("task").GetProperty("size").GetString());

        var failed = await ConsoleRig.Get(rig.Owner, $"/v1/batches/{Batch}/tasks/t2");
        Assert.Equal("session_stalled", failed.GetProperty("result_error").GetProperty("code").GetString());
        Assert.Equal("send it again", failed.GetProperty("result_error").GetProperty("action").GetString());
    }

    [Fact]
    public async Task A_task_goal_is_cut_in_the_batch_and_whole_in_the_task()
    {
        await using var rig = await ConsoleRig.StartAsync();
        var goal = new string('g', 900);
        await ConsoleSeed.BatchAsync(rig, Batch, null, new SeedTask("t1", Goal: goal));

        var inBatch = (await ConsoleRig.Get(rig.Owner, $"/v1/batches/{Batch}")).GetProperty("tasks")[0];
        var inTask = (await ConsoleRig.Get(rig.Owner, $"/v1/batches/{Batch}/tasks/t1")).GetProperty("task");

        Assert.Equal(500, inBatch.GetProperty("goal").GetString()!.Length);
        Assert.True(inBatch.GetProperty("goal_truncated").GetBoolean());
        Assert.Equal(900, inTask.GetProperty("goal").GetString()!.Length);
        Assert.False(inTask.GetProperty("goal_truncated").GetBoolean());
    }

    [Fact]
    public async Task A_task_that_asked_a_question_says_how_to_answer_it_until_a_batch_continues_it()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await ConsoleSeed.BatchAsync(rig, Batch, null, new SeedTask("t1", "needs_input", Goal: "Pick a queue", Questions: ["Which queue?", "Keep the old one?"], Detail: "needs input"));

        var asked = (await ConsoleRig.Get(rig.Owner, $"/v1/batches/{Batch}")).GetProperty("tasks")[0];

        Assert.Equal("needs_input", asked.GetProperty("state").GetString());
        Assert.Equal(["Which queue?", "Keep the old one?"], asked.GetProperty("questions").EnumerateArray().Select(q => q.GetString()));
        Assert.False(asked.GetProperty("answered").GetBoolean());
        var answer = asked.GetProperty("answer");
        Assert.Equal(("POST", "/v1/runs", "driven"), (answer.GetProperty("method").GetString(), answer.GetProperty("path").GetString(), answer.GetProperty("preset").GetString()));
        Assert.Equal((Batch, "t1"), (answer.GetProperty("continue_from").GetProperty("run_id").GetString(), answer.GetProperty("continue_from").GetProperty("task_id").GetString()));
        Assert.Equal(ConsoleRig.Commit, answer.GetProperty("repository").GetProperty("commit").GetString());

        await ConsoleSeed.BatchAsync(rig.Log, "run-b2", null, ConsoleRig.Now.AddMinutes(-30), new SeedTask("t1", Asked: new DrivenTask("t1", ContinueFrom: new ContinueFrom(Batch, "t1"), Answers: ["queue A", "yes"])));
        var answered = (await ConsoleRig.Get(rig.Owner, $"/v1/batches/{Batch}")).GetProperty("tasks")[0];
        Assert.True(answered.GetProperty("answered").GetBoolean());
        Assert.False(answered.TryGetProperty("answer", out _));
        Assert.Equal(("run-b2", "t1"), (answered.GetProperty("answered_by")[0].GetProperty("run_id").GetString(), answered.GetProperty("answered_by")[0].GetProperty("task_id").GetString()));

        var continuing = (await ConsoleRig.Get(rig.Owner, "/v1/batches/run-b2")).GetProperty("tasks")[0];
        Assert.Equal((Batch, "t1"), (continuing.GetProperty("continues").GetProperty("run_id").GetString(), continuing.GetProperty("continues").GetProperty("task_id").GetString()));
    }

    [Fact]
    public async Task An_account_is_told_its_question_was_answered_but_not_which_batch_of_another_account_answered_it()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()]);
        await ConsoleSeed.BatchAsync(rig.Log, "run-aq", "alice", ConsoleRig.Now.AddHours(-5), new SeedTask("t1", "needs_input", Questions: ["Which?"]));
        // The owner continues alice's task; the owner's batch is not alice's to see.
        await ConsoleSeed.BatchAsync(rig.Log, "run-owner-answer", null, ConsoleRig.Now.AddHours(-1), new SeedTask("t1", Asked: new DrivenTask("t1", ContinueFrom: new ContinueFrom("run-aq", "t1"), Answers: ["A"])));
        using var alice = rig.As("alice");

        var forAlice = (await ConsoleRig.Get(alice, "/v1/batches/run-aq")).GetProperty("tasks")[0];
        var forOwner = (await ConsoleRig.Get(rig.Owner, "/v1/batches/run-aq")).GetProperty("tasks")[0];

        Assert.True(forAlice.GetProperty("answered").GetBoolean());
        Assert.Equal(0, forAlice.GetProperty("answered_by").GetArrayLength());
        Assert.False(forAlice.TryGetProperty("answer", out _));
        Assert.Equal(["run-owner-answer"], forOwner.GetProperty("answered_by").EnumerateArray().Select(a => a.GetProperty("run_id").GetString()));
        Assert.DoesNotContain("run-owner-answer", await ConsoleRig.Text(alice, "/v1/batches/run-aq"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_run_and_a_run_that_is_not_a_batch_are_404()
    {
        await using var rig = await ConsoleRig.StartAsync();
        var single = ConsoleSeed.Start("run-single", Runs.DraftRequest(), null, ConsoleRig.Now.AddHours(-1));
        await rig.Log.AppendAsync(single, default);
        await ConsoleSeed.BatchAsync(rig, Batch, null, new SeedTask("t1"));

        Assert.Equal(HttpStatusCode.NotFound, await ConsoleRig.Status(rig.Owner, "/v1/batches/run-none"));
        Assert.Equal(HttpStatusCode.NotFound, await ConsoleRig.Status(rig.Owner, "/v1/batches/run-single"));
        Assert.Equal(HttpStatusCode.NotFound, await ConsoleRig.Status(rig.Owner, $"/v1/batches/{Batch}/tasks/t9"));
        Assert.Equal(HttpStatusCode.NotFound, await ConsoleRig.Status(rig.Owner, "/v1/batches/run-none/tasks/t1"));
        Assert.Equal(HttpStatusCode.BadRequest, await ConsoleRig.Status(rig.Owner, $"/v1/batches/{Batch}?events=5000"));
        Assert.Equal(HttpStatusCode.BadRequest, await ConsoleRig.Status(rig.Owner, $"/v1/batches/{Batch}/tasks/t1?events=-1"));
    }

    [Fact]
    public async Task An_account_sees_its_own_batch_and_task_and_not_another_accounts_or_the_owners()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice(), Bob()]);
        await SeedFinishedAsync(rig, "alice");
        await ConsoleSeed.BatchAsync(rig.Log, "run-owner", null, ConsoleRig.Now.AddHours(-3), new SeedTask("t1"));
        using var alice = rig.As("alice");
        using var bob = rig.As("bob");

        Assert.Equal(HttpStatusCode.OK, await ConsoleRig.Status(alice, $"/v1/batches/{Batch}"));
        Assert.Equal(HttpStatusCode.OK, await ConsoleRig.Status(alice, $"/v1/batches/{Batch}/tasks/t1"));
        Assert.Equal(HttpStatusCode.NotFound, await ConsoleRig.Status(bob, $"/v1/batches/{Batch}"));
        Assert.Equal(HttpStatusCode.NotFound, await ConsoleRig.Status(bob, $"/v1/batches/{Batch}/tasks/t1"));
        Assert.Equal(HttpStatusCode.NotFound, await ConsoleRig.Status(alice, "/v1/batches/run-owner"));
        Assert.Equal(HttpStatusCode.OK, await ConsoleRig.Status(rig.Owner, $"/v1/batches/{Batch}"));
        Assert.Equal(HttpStatusCode.OK, await ConsoleRig.Status(rig.Owner, "/v1/batches/run-owner"));
    }

    [Fact]
    public async Task An_unfinished_batch_reads_from_its_saved_state_with_each_tasks_phase_sessions_and_container()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()]);
        var request = ConsoleSeed.BatchRequest(new DrivenTask("t1", Goal: "one"), new DrivenTask("t2", Goal: "two", DependsOn: ["t1"]), new DrivenTask("t3", Goal: "three"));
        await rig.Log.AppendAsync(ConsoleSeed.Start("run-live", request, "alice", ConsoleRig.Now.AddMinutes(-20)), default);
        var started = ConsoleRig.Now.AddMinutes(-19);
        new BatchStateStore(rig.BatchDirectory).Save(new BatchState("run-live", started, new string('0', 32), request, "token-key-do-not-return",
        [
            new TaskEntry("t1", null, "one", TaskPhase.Started, "run-s2", "chargehand/run-s1", "chargehand-run-s2", started, Continuation: 1, Runs: ["run-s1", "run-s2"], Size: "L", SizeFrom: "plan"),
            new TaskEntry("t2", null, "two", DependsOn: ["t1"]),
            new TaskEntry("t3", null, "three", TaskPhase.HandedOver, "run-s3", "chargehand/run-s3", Started: started,
                Outcome: new TaskOutcome("t3", TaskState.Completed, "run-s3", null, null, 0, 900, "chargehand/run-s3", "https://example.test/o/r/pull/3")),
        ], [], Actor: "alice"));
        using var alice = rig.As("alice");

        var text = await ConsoleRig.Text(alice, "/v1/batches/run-live");
        var body = JsonSerializer.Deserialize<JsonElement>(text);

        Assert.Equal("running", body.GetProperty("run").GetProperty("status").GetString());
        var tasks = body.GetProperty("tasks").EnumerateArray().ToDictionary(t => t.GetProperty("id").GetString()!);
        Assert.Equal(("running", "started", "L", "plan"), (tasks["t1"].GetProperty("state").GetString(), tasks["t1"].GetProperty("phase").GetString(), tasks["t1"].GetProperty("size").GetString(), tasks["t1"].GetProperty("size_from").GetString()));
        Assert.Equal("chargehand-run-s2", tasks["t1"].GetProperty("container").GetString());
        Assert.Equal([("run-s1", "first"), ("run-s2", "continuation")], tasks["t1"].GetProperty("sessions").EnumerateArray().Select(s => (s.GetProperty("run_id").GetString()!, s.GetProperty("role").GetString()!)));
        Assert.Equal(("pending", "pending"), (tasks["t2"].GetProperty("state").GetString(), tasks["t2"].GetProperty("phase").GetString()));
        Assert.Equal(["t1"], tasks["t2"].GetProperty("depends_on").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(("completed", "https://example.test/o/r/pull/3"), (tasks["t3"].GetProperty("state").GetString(), tasks["t3"].GetProperty("pr_url").GetString()));
        Assert.DoesNotContain("token-key-do-not-return", text, StringComparison.Ordinal);
        Assert.DoesNotContain("token-key-do-not-return", await ConsoleRig.Text(alice, "/v1/batches/run-live/tasks/t1"), StringComparison.Ordinal);
    }

    private static RunRequest DrivenRequest(params string[] goals) =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", "abc1234def5678abc1234def5678abc1234def56")),
            Driven: new RequestDriven([.. goals.Select((g, i) => new DrivenTask($"t{i + 1}", Goal: g))], MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    [Fact]
    public async Task A_batch_run_by_the_server_keeps_its_events_after_it_finishes_and_filters_them_per_task()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply),
            driven: (root, log) => new DrivenRunTests.Rig(mcpForward: "chargehand-host:4300", dir: root, log: log, durable: true, learning: new JsonlLearningStore(Path.Combine(root, "learning.jsonl"))).Run);
        var post = await s.PostAsync(JsonContent.Create(DrivenRequest("Add a retry", "Add a backoff"), options: ContractJson.Options), waitSeconds: 30);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var id = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("task_id").GetString()!;

        var body = await ConsoleRig.Get(s.Http, $"/v1/batches/{id}");

        Assert.Equal("completed", body.GetProperty("run").GetProperty("status").GetString());
        var timeline = body.GetProperty("timeline");
        Assert.True(timeline.GetProperty("available").GetBoolean());
        var kinds = timeline.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("event").GetString()).ToList();
        Assert.Contains("container_started", kinds);
        Assert.Contains("pr_opened", kinds);
        Assert.Equal("run_finished", kinds[^1]);
        Assert.All(timeline.GetProperty("events").EnumerateArray(), e => Assert.False(e.TryGetProperty("result", out _)));
        var tasks = body.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal(["completed", "completed"], tasks.Select(t => t.GetProperty("state").GetString()));
        Assert.All(tasks, t =>
        {
            Assert.Equal("flat", t.GetProperty("budget").GetProperty("source").GetProperty("kind").GetString());
            Assert.True(t.GetProperty("spend").GetProperty("input_tokens").GetInt64() > 0);
            Assert.StartsWith("https://example.test/o/r/pull/", t.GetProperty("pr_url").GetString(), StringComparison.Ordinal);
        });
        Assert.Equal(2, body.GetProperty("pull_requests").GetArrayLength());

        var first = tasks[0].GetProperty("id").GetString();
        var task = await ConsoleRig.Get(s.Http, $"/v1/batches/{id}/tasks/{first}");
        var taskEvents = task.GetProperty("timeline").GetProperty("events").EnumerateArray().ToList();
        Assert.NotEmpty(taskEvents);
        Assert.All(taskEvents, e => Assert.Equal(first, e.GetProperty("task_id").GetString()));
        Assert.True(task.GetProperty("verification").GetProperty("passed").GetBoolean());

        var cut = await ConsoleRig.Get(s.Http, $"/v1/batches/{id}?events=1");
        Assert.Equal(1, cut.GetProperty("timeline").GetProperty("events").GetArrayLength());
        Assert.Equal(timeline.GetProperty("total").GetInt32(), cut.GetProperty("timeline").GetProperty("total").GetInt32());
        Assert.Equal(0, (await ConsoleRig.Get(s.Http, $"/v1/batches/{id}?events=0")).GetProperty("timeline").GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task The_server_keeps_the_events_of_its_last_25_finished_runs_only()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        List<string> ids = [];
        for (var i = 0; i < RunService.ArchivedRuns + 2; i++)
        {
            var res = await s.PostAsync(Runs.DraftRequest(), waitSeconds: 30);
            ids.Add((await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("task_id").GetString()!);
        }

        // The slot of a run is freed as it ends; wait until the last archive is in place.
        for (var i = 0; i < 100 && s.Service.Events(ids[^1]) is null; i++)
            await Task.Delay(20);

        Assert.Null(s.Service.Events(ids[0]));
        Assert.Null(s.Service.Events(ids[1]));
        Assert.Equal(RunEventKind.RunFinished, s.Service.Events(ids[2])![^1].Event);
        Assert.Equal(RunEventKind.RunFinished, s.Service.Events(ids[^1])![^1].Event);
        Assert.Null(s.Service.Events("run-unknown"));
    }
}
