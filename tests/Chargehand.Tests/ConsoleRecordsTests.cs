using System.Net;
using System.Text.Json;
using Chargehand.Automation;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Tasks.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>CHARGEHAND-214: what the console reads about a batch that survives a restart, from the records the server keeps: the timeline, every session of a task, the governor's whole log and the
/// pull request's checks, and what intake did with a trigger. Access is the batch's: an account never reads another account's records.</summary>
public class ConsoleRecordsTests
{
    private const string Batch = "run-b1";
    private static readonly DateTimeOffset At = ConsoleRig.Now.AddHours(-2);

    private static AccountSettings Alice() => new("alice", Driven: true, AllowServerCredential: true);

    private static AccountSettings Bob() => new("bob", Driven: true, AllowServerCredential: true);

    private static RunStatus Event(RunEventKind kind, string? task, string detail, int minutes) =>
        RunStatus.Of(Batch, RunState.Running, kind) with { At = At.AddMinutes(minutes), TaskId = task, Detail = detail };

    private static GovernorDecision Decision(int turn, string signal, GovernorAction action, bool acted = false, decimal? spend = null) =>
        new(At.AddMinutes(turn), turn, GovernorMode.Enforce, action, signal, action == GovernorAction.Warn ? "first_hit" : "spend_checkpoint", new Dictionary<string, string> { ["calls"] = "4" }, spend, 4m, "M", acted,
            new BudgetSource("history", 22, 80, "repository"));

    private static async Task SeedAsync(ConsoleRig rig, string? actor = null)
    {
        await ConsoleSeed.BatchAsync(rig, Batch, actor,
            new SeedTask("t1", Goal: "Add a retry", PrUrl: "https://example.test/o/r/pull/7", Branch: "chargehand/run-b1-t1"),
            new SeedTask("t2", "failed", Goal: "Add a backoff", Error: ErrorCode.SessionStalled, Detail: "stalled"));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned(Batch, "t1", actor: actor, pr: ConsoleSeed.Pull(7), continuations: 1,
            governor: [new GovernorCount("repeated_call", "warn", 1, 0), new GovernorCount("spend", "checkpoint", 1, 1)]));
        var records = rig.Records;
        records.AppendEvent(Batch, Event(RunEventKind.Started, null, "", 0));
        records.AppendEvent(Batch, Event(RunEventKind.ContainerStarted, "t1", "task t1: session container started", 1));
        records.AppendEvent(Batch, Event(RunEventKind.ContainerStarted, "t2", "task t2: session container started", 1));
        records.AppendEvent(Batch, Event(RunEventKind.Governor, "t1", "task t1: governor warn on repeated_call", 5));
        records.AppendEvent(Batch, Event(RunEventKind.PrOpened, "t1", "task t1: draft pull request opened", 20));
        records.AppendEvent(Batch, Event(RunEventKind.TaskFinished, "t1", "task t1: completed", 21));
        records.AppendEvent(Batch, RunStatus.Of(Batch, RunState.Failed, RunEventKind.RunFinished) with { At = At.AddMinutes(30), Result = ConsoleSeed.Result(Batch, ResultStatus.Failed, "some tasks did not finish") });
        records.Append(Batch, new SessionRecord("t1", "run-b1-s1", "first", "mac", At.AddMinutes(1), At.AddMinutes(10), "stalled", "checkpoint_spend", 40, 1000, 200, 3000, 400, 2.5m, null, 0, 3));
        records.Append(Batch, new SessionRecord("t1", "run-b1-t1", "continuation", "mac", At.AddMinutes(10), At.AddMinutes(18), "completed", null, 20, 500, 100, 1500, 200, 1.0m, null, 0, 5));
        records.Append(Batch, new SessionRecord("t1", "run-b1-fix1", "ci_fix", "vps", At.AddMinutes(25), At.AddMinutes(27), "completed", null, 6, 100, 20, 300, 50, 0.2m, null, 0, 6));
        records.Append(Batch, new GovernorRecord("t1", "run-b1-s1", 2, [Decision(5, "repeated_call", GovernorAction.Warn), Decision(9, "spend", GovernorAction.Checkpoint, acted: true, spend: 3.4m)]));
        records.Append(Batch, new ChecksRecord("t1", At.AddMinutes(22), 7, "head0", "BLOCKED", false, true, "fix", [new CheckRecord("build", true, "failed", 900), new CheckRecord("lint", false, "passed", 901)]));
        records.Append(Batch, new ChecksRecord("t1", At.AddMinutes(29), 7, "head1", "CLEAN", false, true, "done", [new CheckRecord("build", true, "passed", 900)]));
        records.Append(Batch, new CiAttemptRecord("t1", 1, 3, At.AddMinutes(23), "abcdef012345", ["build"], true, "tests passed"));
    }

    private static string[] Kinds(JsonElement timeline) => [.. timeline.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("event").GetString()!)];

    [Fact]
    public async Task A_finished_batch_reads_its_timeline_from_the_records_with_no_result_and_with_the_source()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await SeedAsync(rig);

        var timeline = (await ConsoleRig.Get(rig.Owner, $"/v1/batches/{Batch}")).GetProperty("timeline");

        Assert.True(timeline.GetProperty("available").GetBoolean());
        Assert.Equal("records", timeline.GetProperty("source").GetString());
        Assert.Equal(7, timeline.GetProperty("total").GetInt32());
        Assert.Equal(["started", "container_started", "container_started", "governor", "pr_opened", "task_finished", "run_finished"], Kinds(timeline));
        Assert.All(timeline.GetProperty("events").EnumerateArray(), e => Assert.False(e.TryGetProperty("result", out _)));
    }

    [Fact]
    public async Task The_task_view_filters_the_recorded_timeline_to_the_task_and_the_events_query_cuts_it()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await SeedAsync(rig);

        var task = (await ConsoleRig.Get(rig.Owner, $"/v1/batches/{Batch}/tasks/t1")).GetProperty("timeline");
        var cut = (await ConsoleRig.Get(rig.Owner, $"/v1/batches/{Batch}?events=2")).GetProperty("timeline");

        Assert.Equal(["container_started", "governor", "pr_opened", "task_finished"], Kinds(task));
        Assert.Equal(("t1", 4), (task.GetProperty("events")[0].GetProperty("task_id").GetString(), task.GetProperty("total").GetInt32()));
        Assert.Equal(["task_finished", "run_finished"], Kinds(cut));
        Assert.Equal(7, cut.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_batch_with_no_records_is_unchanged_its_timeline_is_unavailable_and_its_sessions_come_from_the_run_log()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await ConsoleSeed.BatchAsync(rig, "run-old", null, new SeedTask("t1"));

        var body = await ConsoleRig.Get(rig.Owner, "/v1/batches/run-old");

        Assert.False(body.GetProperty("timeline").GetProperty("available").GetBoolean());
        Assert.False(body.GetProperty("timeline").TryGetProperty("source", out _));
        var session = Assert.Single(body.GetProperty("tasks")[0].GetProperty("sessions").EnumerateArray());
        Assert.Equal(("run-old-t1", "first"), (session.GetProperty("run_id").GetString(), session.GetProperty("role").GetString()));
        Assert.False(session.TryGetProperty("started", out _));
        Assert.False(body.GetProperty("tasks")[0].TryGetProperty("ci", out _));
        Assert.False(body.GetProperty("tasks")[0].TryGetProperty("governor", out _));
    }

    [Fact]
    public async Task Every_session_of_a_task_is_listed_in_order_with_its_runner_reason_and_numbers()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await SeedAsync(rig);

        var sessions = (await ConsoleRig.Get(rig.Owner, $"/v1/batches/{Batch}")).GetProperty("tasks")[0].GetProperty("sessions").EnumerateArray().ToList();

        Assert.Equal([("run-b1-s1", "first"), ("run-b1-t1", "continuation"), ("run-b1-fix1", "ci_fix")], sessions.Select(s => (s.GetProperty("run_id").GetString()!, s.GetProperty("role").GetString()!)));
        var first = sessions[0];
        Assert.Equal(("mac", "stalled", "checkpoint_spend", 40), (first.GetProperty("runner").GetString(), first.GetProperty("status").GetString(), first.GetProperty("reason").GetString(), first.GetProperty("turns").GetInt32()));
        Assert.Equal((1000, 200, 3000, 400, 2.5m, 3), (first.GetProperty("input_tokens").GetInt32(), first.GetProperty("output_tokens").GetInt32(), first.GetProperty("cache_read_tokens").GetInt32(),
            first.GetProperty("cache_write_tokens").GetInt32(), first.GetProperty("usd_estimate").GetDecimal(), first.GetProperty("commits").GetInt32()));
        Assert.Equal(At.AddMinutes(1), first.GetProperty("started").GetDateTimeOffset());
        Assert.Equal(At.AddMinutes(10), first.GetProperty("ended").GetDateTimeOffset());
        Assert.False(first.TryGetProperty("usd", out _));
        Assert.False(sessions[1].TryGetProperty("reason", out _));
        Assert.Equal("vps", sessions[2].GetProperty("runner").GetString());
    }

    [Fact]
    public async Task The_governor_decisions_come_in_full_beside_the_counts_in_the_batch_and_the_task()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await SeedAsync(rig);

        foreach (var governor in new[]
        {
            (await ConsoleRig.Get(rig.Owner, $"/v1/batches/{Batch}")).GetProperty("tasks")[0].GetProperty("governor"),
            (await ConsoleRig.Get(rig.Owner, $"/v1/batches/{Batch}/tasks/t1")).GetProperty("task").GetProperty("governor"),
        })
        {
            Assert.Equal(("observe", 2), (governor.GetProperty("mode").GetString(), governor.GetProperty("decisions").GetArrayLength()));
            Assert.Equal(2, governor.GetProperty("log_total").GetInt32());
            var log = governor.GetProperty("log").EnumerateArray().ToList();
            Assert.Equal(["repeated_call:warn:first_hit:False", "spend:checkpoint:spend_checkpoint:True"], log.Select(d => $"{d.GetProperty("signal").GetString()}:{d.GetProperty("action").GetString()}:{d.GetProperty("rule").GetString()}:{d.GetProperty("acted").GetBoolean()}"));
            Assert.Equal(("run-b1-s1", 9, "enforce", 3.4m, 4m), (log[1].GetProperty("session").GetString(), log[1].GetProperty("turn").GetInt32(), log[1].GetProperty("mode").GetString(),
                log[1].GetProperty("spend_usd").GetDecimal(), log[1].GetProperty("budget_usd").GetDecimal()));
            Assert.Equal("4", log[0].GetProperty("values").GetProperty("calls").GetString());
            Assert.Equal(("history", 22), (log[0].GetProperty("budget_source").GetProperty("kind").GetString(), log[0].GetProperty("budget_source").GetProperty("n").GetInt32()));
        }
    }

    [Fact]
    public async Task A_task_that_is_still_running_shows_its_governor_log_with_counts_taken_from_it()
    {
        await using var rig = await ConsoleRig.StartAsync();
        var request = ConsoleSeed.BatchRequest(new DrivenTask("t1", Goal: "one"));
        await rig.Log.AppendAsync(ConsoleSeed.Start("run-live", request, null, ConsoleRig.Now.AddMinutes(-20)), default);
        var started = ConsoleRig.Now.AddMinutes(-19);
        await OpenBatchSeed.SaveAsync(rig.BatchDirectory, rig.Log, new OpenBatch("run-live", started, new string('0', 32), request,
            [new TaskEntry("t1", null, "one", TaskPhase.Started, "run-s2", "chargehand/run-s1", "chargehand-run-s2", started, Continuation: 1, Runs: ["run-s1", "run-s2"])], []), rig.Driven);
        rig.Records.Append("run-live", new SessionRecord("t1", "run-s1", "first", null, started, started.AddMinutes(8), "stalled", "checkpoint_stuck", 50, 10, 10, 10, 10, null, null, 0, 2));
        rig.Records.Append("run-live", new GovernorRecord("t1", "run-s1", 3, [Decision(1, "repeated_call", GovernorAction.Warn), Decision(2, "repeated_call", GovernorAction.Warn), Decision(3, "repeated_call", GovernorAction.Checkpoint, acted: true)]));

        var text = await ConsoleRig.Text(rig.Owner, "/v1/batches/run-live");
        var task = JsonSerializer.Deserialize<JsonElement>(text).GetProperty("tasks")[0];

        // The recorded session carries its numbers; the one that has begun is named by the saved state alone.
        var sessions = task.GetProperty("sessions").EnumerateArray().ToList();
        Assert.Equal([("run-s1", "first"), ("run-s2", "continuation")], sessions.Select(s => (s.GetProperty("run_id").GetString()!, s.GetProperty("role").GetString()!)));
        Assert.Equal("checkpoint_stuck", sessions[0].GetProperty("reason").GetString());
        Assert.False(sessions[1].TryGetProperty("status", out _));
        var governor = task.GetProperty("governor");
        Assert.Equal(["repeated_call:checkpoint:1:1", "repeated_call:warn:2:0"], governor.GetProperty("decisions").EnumerateArray()
            .Select(d => $"{d.GetProperty("signal").GetString()}:{d.GetProperty("action").GetString()}:{d.GetProperty("count").GetInt32()}:{d.GetProperty("acted").GetInt32()}"));
        Assert.Equal(3, governor.GetProperty("log").GetArrayLength());
        Assert.DoesNotContain("token-key-do-not-return", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_latest_look_at_the_checks_and_every_ci_fix_attempt_are_in_the_batch_and_the_task_has_the_looks_too()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await SeedAsync(rig);

        var inBatch = (await ConsoleRig.Get(rig.Owner, $"/v1/batches/{Batch}")).GetProperty("tasks")[0].GetProperty("ci");
        var inTask = (await ConsoleRig.Get(rig.Owner, $"/v1/batches/{Batch}/tasks/t1")).GetProperty("task").GetProperty("ci");

        Assert.Equal(2, inBatch.GetProperty("looks").GetInt32());
        var latest = inBatch.GetProperty("checks");
        Assert.Equal(("head1", "CLEAN", "done", 7), (latest.GetProperty("head_sha").GetString(), latest.GetProperty("merge_status").GetString(), latest.GetProperty("decision").GetString(), latest.GetProperty("pull").GetInt32()));
        Assert.Equal([("build", true, "passed", 900)], latest.GetProperty("checks").EnumerateArray().Select(c => (c.GetProperty("name").GetString()!, c.GetProperty("required").GetBoolean(), c.GetProperty("outcome").GetString()!, c.GetProperty("job_id").GetInt32())));
        var attempt = Assert.Single(inBatch.GetProperty("attempts").EnumerateArray());
        Assert.Equal((1, 3, true, "tests passed"), (attempt.GetProperty("attempt").GetInt32(), attempt.GetProperty("max_attempts").GetInt32(), attempt.GetProperty("pushed").GetBoolean(), attempt.GetProperty("detail").GetString()));
        Assert.Equal(["build"], attempt.GetProperty("failing").EnumerateArray().Select(f => f.GetString()));
        Assert.False(inBatch.TryGetProperty("history", out _));

        Assert.Equal(["head0", "head1"], inTask.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("head_sha").GetString()));
        Assert.Equal(["failed", "passed"], inTask.GetProperty("history")[0].GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("outcome").GetString()));
    }

    [Fact]
    public async Task An_account_reads_the_records_of_its_own_batch_and_another_account_gets_a_not_found_for_all_of_them()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice(), Bob()]);
        await SeedAsync(rig, "alice");
        using var alice = rig.As("alice");
        using var bob = rig.As("bob");

        foreach (var route in new[] { $"/v1/batches/{Batch}", $"/v1/batches/{Batch}/tasks/t1", $"/v1/batches/{Batch}/tasks/t1?events=2" })
        {
            Assert.Equal(HttpStatusCode.OK, await ConsoleRig.Status(alice, route));
            Assert.Equal(HttpStatusCode.OK, await ConsoleRig.Status(rig.Owner, route));
            Assert.Equal(HttpStatusCode.NotFound, await ConsoleRig.Status(bob, route));
        }
        var bobsView = await ConsoleRig.Text(bob, "/v1/overview");
        Assert.DoesNotContain(Batch, bobsView, StringComparison.Ordinal);
        Assert.Contains("run-b1-fix1", await ConsoleRig.Text(alice, $"/v1/batches/{Batch}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_batch_id_that_would_leave_the_records_directory_reads_nothing_and_is_not_found()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await SeedAsync(rig);

        Assert.Equal(HttpStatusCode.NotFound, await ConsoleRig.Status(rig.Owner, "/v1/batches/..%2F..%2Fetc"));
        Assert.Equal(HttpStatusCode.NotFound, await ConsoleRig.Status(rig.Owner, "/v1/batches/run-b1%2F..%2Frun-b1"));
    }

    // Intake

    private static Profile IntakeProfile(string root) => Runs.Profile(root) with
    {
        Automation = new AutomationSettings(new AutomationGitHub(new Dictionary<string, AutomationRepository> { ["o/r"] = new("/srv/checkouts/r") }, ["octo"])),
    };

    [Fact]
    public async Task Intake_lists_what_it_did_with_each_trigger_newest_first_with_the_reason_and_the_login_and_no_text()
    {
        await using var rig = await ConsoleRig.StartAsync(profile: IntakeProfile);
        var state = IntakeState.Load(rig.IntakePath);
        state.Triggers.Add(new IntakeTrigger(ConsoleRig.Now.AddHours(-3), "label:o/r#12:1", "started"));
        state.Triggers.Add(new IntakeTrigger(ConsoleRig.Now.AddHours(-2), "comment:o/r:5", "ignored", "author_not_allowed", "mallory"));
        state.Triggers.Add(new IntakeTrigger(ConsoleRig.Now.AddHours(-1), "comment:o/r:6", "ignored", "cap"));
        state.Save(ConsoleRig.Now);

        var text = await ConsoleRig.Text(rig.Owner, "/v1/intake");
        var triggers = JsonSerializer.Deserialize<JsonElement>(text).GetProperty("triggers").EnumerateArray().ToList();

        Assert.Equal(["comment:o/r:6", "comment:o/r:5", "label:o/r#12:1"], triggers.Select(t => t.GetProperty("trigger").GetString()));
        Assert.Equal(("ignored", "author_not_allowed", "mallory"), (triggers[1].GetProperty("outcome").GetString(), triggers[1].GetProperty("reason").GetString(), triggers[1].GetProperty("author").GetString()));
        Assert.Equal(ConsoleRig.Now.AddHours(-1), triggers[0].GetProperty("at").GetDateTimeOffset());
        Assert.False(triggers[2].TryGetProperty("reason", out _));
    }

    [Fact]
    public async Task Intake_with_no_log_has_no_triggers_and_an_account_still_may_not_read_it()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()], profile: IntakeProfile);

        var body = await ConsoleRig.Get(rig.Owner, "/v1/intake");

        Assert.Equal(0, body.GetProperty("triggers").GetArrayLength());
        using var alice = rig.As("alice");
        Assert.Equal(HttpStatusCode.Forbidden, await ConsoleRig.Status(alice, "/v1/intake"));
    }

    // API keys

    [Fact]
    public async Task A_key_shows_when_it_was_last_used_as_a_time_alone_and_a_key_never_used_shows_none()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()]);
        var used = rig.Server.Keys.Create("alice", "laptop");
        var idle = rig.Server.Keys.Create("alice", "ci");
        using var alice = rig.Server.As(used.Key);
        await ConsoleRig.Status(alice, "/v1/overview");

        var text = await ConsoleRig.Text(rig.Owner, "/v1/accounts");
        var keys = JsonSerializer.Deserialize<JsonElement>(text).GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "alice").GetProperty("keys").EnumerateArray()
            .ToDictionary(k => k.GetProperty("id").GetString()!);

        Assert.Equal(ConsoleRig.Now, keys[used.Id].GetProperty("last_used").GetDateTimeOffset());
        Assert.False(keys[idle.Id].TryGetProperty("last_used", out _));
        Assert.DoesNotContain(used.Key, text, StringComparison.Ordinal);
        // The file holds the id and a time and nothing else: no key, no salt, no hash, no address.
        var file = File.ReadAllText(AccountKeyUse.FileFor(Path.Combine(rig.Root, "accounts.json")));
        Assert.Contains(used.Id, file, StringComparison.Ordinal);
        Assert.DoesNotContain(used.Key.Split('.')[2], file, StringComparison.Ordinal);
        foreach (var stored in rig.Server.Keys.List())
        {
            Assert.DoesNotContain(stored.Hash, file, StringComparison.Ordinal);
            Assert.DoesNotContain(stored.Salt, file, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_revoked_key_or_a_wrong_one_leaves_no_time_and_the_owners_key_is_not_a_key_of_an_account()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()]);
        var revoked = rig.Server.Keys.Create("alice", "old");
        rig.Server.Keys.Revoke(revoked.Id);
        using var old = rig.Server.As(revoked.Key);
        using var wrong = rig.Server.As("chk1.0000000000000000.nope");

        Assert.Equal(HttpStatusCode.Unauthorized, await ConsoleRig.Status(old, "/v1/overview"));
        Assert.Equal(HttpStatusCode.Unauthorized, await ConsoleRig.Status(wrong, "/v1/overview"));
        await ConsoleRig.Status(rig.Owner, "/v1/overview");

        Assert.False(File.Exists(AccountKeyUse.FileFor(Path.Combine(rig.Root, "accounts.json"))));
    }
}
