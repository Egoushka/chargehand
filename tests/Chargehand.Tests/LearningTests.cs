using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Kernel;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>ADR 0045's learning loop: a record per finished task, the outcome of its pull request read back through the forge, the report over the records, and the retro after a batch (off by
/// default). The sessions are scripted by the fake output volume and the forge by <see cref="FakeIntegrationHost"/>; nothing starts a container or reaches a network.</summary>
public class LearningTests
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";
    private const string BatchId = "run-batch-l";
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private static RunRequest Request(params DrivenTask[] tasks) =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([.. tasks], MaxParallel: 1, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    private static RunRequest One(DrivenTask? task = null) => Request(task ?? new DrivenTask("t1", Goal: "one"));

    private static SessionOutcome Done(decimal? usd = null, int commits = 4, IReadOnlyList<GovernorDecision>? governor = null) =>
        new(SessionStatus.Completed, "", 30, 1000, 200, 4000, null, 0, [], true, CacheWriteTokens: 300, Commits: commits, UsdEstimate: usd, Governor: governor);

    private static SessionOutcome Stopped(SessionStatus status, string reason, decimal? usd = null, IReadOnlyList<GovernorDecision>? governor = null) =>
        new(status, reason, 80, 1000, 200, 4000, null, 0, [], true, Commits: 3, UsdEstimate: usd, Governor: governor);

    private static GovernorDecision Decision(GovernorAction action, string signal, bool acted = false) =>
        new(Now, 7, GovernorMode.Observe, action, signal, "first_hit", new Dictionary<string, string>(), 1.2m, 10m, "L", acted);

    private static bool IsContinuation(string goal) => goal.Contains("Continuation ", StringComparison.Ordinal);

    private sealed class Stores : IDisposable
    {
        public TempDir Dir { get; } = new();

        public JsonlLearningStore Store => new(Path.Combine(Dir.Path, "learning.jsonl"));

        public void Dispose() => Dir.Dispose();
    }

    private static async Task<IReadOnlyList<LearningRecord>> Records(ILearningStore store) => [.. (await store.ReadAsync(default)).Select(e => e.Record)];

    private static async Task<LearningRecord> RecordOf(Func<string, string, SessionOutcome?> outcomes, DrivenTask? task = null)
    {
        using var stores = new Stores();
        using var rig = new DrivenRunTests.Rig(learning: stores.Store, outcomes: outcomes);
        await rig.Run.RunAsync(One(task), BatchId, Token, default);
        return Assert.Single(await Records(stores.Store));
    }

    [Fact]
    public async Task A_completed_task_leaves_a_record_with_its_size_budget_spend_governor_counts_and_pull_request()
    {
        using var stores = new Stores();
        var governor = new[] { Decision(GovernorAction.Warn, "repeated_call"), Decision(GovernorAction.Checkpoint, "repeated_call"), Decision(GovernorAction.Warn, "environment_probing") };
        using var rig = new DrivenRunTests.Rig(learning: stores.Store, outcomes: (_, _) => Done(2.5m, governor: governor));

        await rig.Run.RunAsync(One(new DrivenTask("t1", Goal: "one", Size: "L")), BatchId, Token, default);

        var r = Assert.Single(await Records(stores.Store));
        Assert.Equal((BatchId, "t1", rig.Engine.Started[0].RunId), (r.BatchId, r.TaskId, r.RunId));
        Assert.Equal("o/r", r.Repository);
        Assert.Equal(("L", "caller"), (r.SizeClass, r.SizeFrom));
        Assert.Equal(new LearningBudget(10m, new BudgetSource("flat")), r.Budget);
        Assert.Equal(new LearningSpend(2.5m, false, 1000, 200, 4000, 300, 30, 0), r.Spend);
        Assert.Equal((LearningOutcome.Completed, "completed", "observe", false), (r.Outcome, r.StopReason, r.GovernorMode, r.TestIntegrityFlagged));
        Assert.Equal([new GovernorCount("environment_probing", "warn", 1, 0), new GovernorCount("repeated_call", "checkpoint", 1, 0), new GovernorCount("repeated_call", "warn", 1, 0)], r.Governor);
        Assert.True(r.Checkpointed);
        Assert.Equal("https://example.test/o/r/pull/1", r.Pr!.Url);
        Assert.Equal((1, "o", "r", Commit), (r.Pr.Number, r.Pr.Remote.Owner, r.Pr.Remote.Name, r.Pr.HeadSha));
        Assert.True(r.At > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task A_record_names_the_account_that_started_the_batch()
    {
        using var stores = new Stores();
        using var rig = new DrivenRunTests.Rig(learning: stores.Store, outcomes: (_, _) => Done(1m), accounts: [new("alice", AllowServerCredential: true)]);

        await rig.Run.RunAsync(One(), BatchId, Token, default, actor: "alice");

        Assert.Equal("alice", Assert.Single(await Records(stores.Store)).Actor);
    }

    [Fact]
    public async Task A_record_holds_no_goal_and_no_session_text()
    {
        using var stores = new Stores();
        using var rig = new DrivenRunTests.Rig(learning: stores.Store, outcomes: (_, _) => Done(1m));
        await rig.Run.RunAsync(One(new DrivenTask("t1", Goal: "Rotate the SECRET-GOAL-MARKER credentials")), BatchId, Token, default);

        var text = File.ReadAllText(Path.Combine(stores.Dir.Path, "learning.jsonl"));
        Assert.DoesNotContain("SECRET-GOAL-MARKER", text);
        Assert.Single(text.TrimEnd().Split('\n'));
    }

    [Fact]
    public async Task A_size_the_caller_left_out_is_the_default_class_with_the_flat_budget_and_a_budget_of_its_own_is_the_tasks()
    {
        using var stores = new Stores();
        using var rig = new DrivenRunTests.Rig(learning: stores.Store, outcomes: (_, _) => Done(1m));
        await rig.Run.RunAsync(Request(new DrivenTask("t1", Goal: "one"), new DrivenTask("t2", Goal: "two", BudgetUsd: 2.5m)), BatchId, Token, default);

        var byTask = (await Records(stores.Store)).ToDictionary(r => r.TaskId);
        Assert.Equal(("M", "default", 4m, "flat"), (byTask["t1"].SizeClass, byTask["t1"].SizeFrom, byTask["t1"].Budget.Usd, byTask["t1"].Budget.Source.Kind));
        Assert.Equal(("M", "default", 2.5m, "task"), (byTask["t2"].SizeClass, byTask["t2"].SizeFrom, byTask["t2"].Budget.Usd, byTask["t2"].Budget.Source.Kind));
    }

    [Fact]
    public async Task A_size_the_plan_estimated_is_marked_as_the_plans_and_the_callers_stays_the_callers()
    {
        using var stores = new Stores();
        using var rig = new DrivenRunTests.Rig(learning: stores.Store, planner: new SizingPlanner(), outcomes: (_, _) => Done(1m));
        var request = Request(new DrivenTask("t1", Goal: "one"), new DrivenTask("t2", Goal: "two", Size: "L")) with { };
        request = request with { Driven = request.Driven! with { Plan = true } };
        await rig.Run.RunAsync(request, BatchId, Token, default);

        var byTask = (await Records(stores.Store)).ToDictionary(r => r.TaskId);
        Assert.Equal(("S", "plan", 1.5m), (byTask["t1"].SizeClass, byTask["t1"].SizeFrom, byTask["t1"].Budget.Usd));
        Assert.Equal(("L", "caller"), (byTask["t2"].SizeClass, byTask["t2"].SizeFrom));
    }

    private sealed class SizingPlanner : IBatchPlanner
    {
        public Task<PlanProposal> ProposeAsync(PlanInput input, CancellationToken ct) => Task.FromResult(new PlanProposal([new ProposedTask("t1", "S", null, null)], [], null));
    }

    [Fact]
    public async Task A_task_stopped_with_its_work_kept_is_partial_and_names_the_stop_reason()
    {
        var r = await RecordOf((_, _) => Stopped(SessionStatus.TokenCap, "token_cap", 3m));

        Assert.Equal((LearningOutcome.Partial, "token_cap", 3m), (r.Outcome, r.StopReason, r.Spend.UsdEstimate));
        Assert.NotNull(r.Pr);
    }

    [Fact]
    public async Task A_session_that_failed_without_work_is_failed_and_one_that_asked_a_question_needs_input()
    {
        var failed = await RecordOf((_, _) => new SessionOutcome(SessionStatus.Failed, "session_error", 3, 10, 5, 0, null, 1, [], false));
        var asked = await RecordOf((_, _) => new SessionOutcome(SessionStatus.NeedsInput, "", 3, 10, 5, 0, null, 0, ["which database?"], false));

        Assert.Equal((LearningOutcome.Failed, "session_error", null), (failed.Outcome, failed.StopReason, failed.Pr));
        Assert.Equal((LearningOutcome.NeedsInput, "needs_input"), (asked.Outcome, asked.StopReason));
    }

    [Fact]
    public async Task The_record_carries_the_peak_memory_the_engine_measured_and_none_when_it_could_not()
    {
        using var stores = new Stores();
        using var rig = new DrivenRunTests.Rig(learning: stores.Store, outcomes: (_, _) => Done(1m));
        rig.Engine.PeakMb = 289;
        rig.Engine.RunFor = 2;
        await rig.Run.RunAsync(One(), BatchId, Token, default);
        Assert.Equal(289, Assert.Single(await Records(stores.Store)).MemoryPeakMb);

        using var blind = new Stores();
        using var unmeasured = new DrivenRunTests.Rig(learning: blind.Store, outcomes: (_, _) => Done(1m));
        await unmeasured.Run.RunAsync(One(), BatchId, Token, default);
        Assert.Null(Assert.Single(await Records(blind.Store)).MemoryPeakMb);
    }

    [Fact]
    public async Task A_continued_tasks_record_holds_the_largest_peak_of_its_sessions()
    {
        using var stores = new Stores();
        using var rig = new DrivenRunTests.Rig(learning: stores.Store, outcomes: (_, goal) => IsContinuation(goal) ? Done(0.5m) : Stopped(SessionStatus.Stalled, "checkpoint_spend", 1.25m));
        var first = (string?)null;
        rig.Engine.PeakOf = id => (first ??= id) == id ? 2400 : 700;   // the first session was the big one
        await rig.Run.RunAsync(One(), BatchId, Token, default);
        var r = Assert.Single(await Records(stores.Store));
        Assert.Equal(1, r.Spend.Continuations);
        Assert.Equal(2400, r.MemoryPeakMb);
    }

    [Fact]
    public async Task The_progress_event_carries_the_peak_so_far()
    {
        var volumes = new DrivenRunTests.FakeVolumes(_ => SessionStatus.Completed) { Usage = new TaskUsage(48_200, 0, 12, SessionStage.Write) };
        using var rig = new DrivenRunTests.Rig(volumes: volumes);
        rig.Engine.RunFor = 3;
        rig.Engine.PeakMb = 612;
        List<RunStatus> events = [];
        await rig.Run.RunAsync(One(), BatchId, Token, default, e => { lock (events) events.Add(e); });
        Assert.Equal(612, events.Single(e => e.Event == RunEventKind.SessionProgress).MemoryPeakMb);
    }

    [Fact]
    public async Task A_continued_task_adds_its_sessions_spend_and_decisions_and_counts_the_continuation()
    {
        var first = new[] { Decision(GovernorAction.Warn, "repeated_call"), Decision(GovernorAction.Checkpoint, "spend", acted: true) };
        var second = new[] { Decision(GovernorAction.Warn, "repeated_call") };
        var r = await RecordOf((_, goal) => IsContinuation(goal) ? Done(0.5m, governor: second) : Stopped(SessionStatus.Stalled, "checkpoint_spend", 1.25m, first));

        Assert.Equal((LearningOutcome.Completed, "completed", 1), (r.Outcome, r.StopReason, r.Spend.Continuations));
        Assert.Equal((1.75m, 110, 2000L), (r.Spend.UsdEstimate, r.Spend.Turns, r.Spend.InputTokens));
        Assert.Equal([new GovernorCount("repeated_call", "warn", 2, 0), new GovernorCount("spend", "checkpoint", 1, 1)], r.Governor);
        Assert.True(r.Checkpointed);
        Assert.True(r.Completed);
    }

    [Fact]
    public async Task A_branch_that_only_changes_tests_is_flagged_in_the_record()
    {
        using var stores = new Stores();
        using var rig = new DrivenRunTests.Rig(learning: stores.Store, outcomes: (_, _) => Done(1m));
        rig.Handover.Integrity = new TestIntegrityFlag(["tests/a_test.py"], []);
        await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.True(Assert.Single(await Records(stores.Store)).TestIntegrityFlagged);
    }

    private static DrivenIntegrator MergeRepository() => new(1, 2, "app-key", new Dictionary<string, string> { ["o/r"] = "merge" }, null);

    [Fact]
    public async Task The_record_keeps_the_head_chargehand_left_after_updating_the_branch_itself()
    {
        using var stores = new Stores();
        var host = new FakeIntegrationHost { BehindBy = 1 };
        host.Script = h => h.Calls.Any(c => c.StartsWith("auto-merge", StringComparison.Ordinal)) ? h.Current with { Merged = true } : h.Current;
        using var rig = new DrivenRunTests.Rig(learning: stores.Store, integrator: MergeRepository(), integration: host, outcomes: (_, _) => Done(1m));

        await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Contains(host.Calls, c => c.StartsWith("update-branch", StringComparison.Ordinal));
        Assert.Equal("bbbbbbb2", Assert.Single(await Records(stores.Store)).Pr!.HeadSha);
    }

    [Fact]
    public async Task The_record_keeps_the_head_of_the_last_ci_fix_and_the_spend_of_the_fix_sessions_in_one_record_per_task()
    {
        using var stores = new Stores();
        var host = new FakeIntegrationHost();
        host.Logs[900] = "FAILED Retry.Tests";
        using var rig = new DrivenRunTests.Rig(learning: stores.Store, integrator: MergeRepository(), integration: host,
            outcomes: (_, goal) => goal.StartsWith("Fix the failing CI checks", StringComparison.Ordinal) ? Done(0.4m, 5) : Done(1m, 4));
        rig.Handover.CommitFor = n => n == 1 ? Commit : "fe00000000000000000000000000000000000fix";
        host.Script = h =>
        {
            var fixedOnce = rig.Handover.Inputs.Count >= 2;
            return h.Current with { HeadSha = fixedOnce ? "head1" : "head0", Merged = fixedOnce, Checks = [new CheckStatus("build", true, fixedOnce ? CheckOutcome.Passed : CheckOutcome.Failed, 900)] };
        };

        await rig.Run.RunAsync(One(), BatchId, Token, default);

        // The record written when the follow-up began is replaced by the final one, which has the fix session's spend.
        var r = Assert.Single(await Records(stores.Store));
        Assert.Equal("fe00000000000000000000000000000000000fix", r.Pr!.HeadSha);
        Assert.Equal(1.4m, r.Spend.UsdEstimate);
    }

    [Fact]
    public async Task A_store_that_cannot_be_written_does_not_fail_the_task()
    {
        using var rig = new DrivenRunTests.Rig(learning: new BrokenStore(), outcomes: (_, _) => Done(1m));

        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Equal(ResultStatus.Completed, result.Status);
    }

    private sealed class BrokenStore : ILearningStore
    {
        public Task AppendAsync(LearningRecord record, CancellationToken ct) => throw new IOException("disk full");

        public Task AppendAsync(PrOutcomeRecord record, CancellationToken ct) => throw new IOException("disk full");

        public Task<IReadOnlyList<LearningEntry>> ReadAsync(CancellationToken ct) => throw new IOException("disk full");
    }

    [Fact]
    public async Task Without_a_store_nothing_is_written_and_the_task_is_unchanged()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: (_, _) => Done(1m));

        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Null(rig.Run.Learning);
    }

    // The store

    internal static LearningRecord Record(string run = "r1", string repository = "o/r", string size = "M", decimal? usd = 1m, string outcome = LearningOutcome.Completed, bool checkpoint = false,
        DateTimeOffset? at = null, LearningPr? pr = null) =>
        new(BatchId, $"task-{run}", run, at ?? Now.AddHours(-1), repository, size, "caller", new LearningBudget(4m, new BudgetSource("flat")),
            new LearningSpend(usd, false, 10, 5, 0, 0, 3, 0), outcome, outcome == LearningOutcome.Completed ? "completed" : "turn_limit", "observe",
            checkpoint ? [new GovernorCount("spend", "checkpoint", 1, 0)] : [], false, pr);

    private static LearningPr Pr(int number = 7, string? head = "aaaa") => new($"https://example.test/o/r/pull/{number}", number, new RemoteRepository("o", "r"), head);

    [Fact]
    public async Task The_store_keeps_the_latest_record_of_a_run_and_the_latest_outcome_of_its_pull_request()
    {
        using var stores = new Stores();
        var store = stores.Store;
        await store.AppendAsync(Record("r1", usd: 1m), default);
        await store.AppendAsync(Record("r2", usd: 2m, at: Now.AddHours(-2)), default);
        await store.AppendAsync(Record("r1", usd: 1.5m), default);
        await store.AppendAsync(new PrOutcomeRecord("r1", Now, PrOutcomes.Open, null), default);
        await store.AppendAsync(new PrOutcomeRecord("r1", Now, PrOutcomes.MergedUnchanged, "aaaa"), default);

        var entries = await store.ReadAsync(default);

        Assert.Equal(["r2", "r1"], entries.Select(e => e.Record.RunId));
        Assert.Equal(1.5m, entries[1].Record.Spend.UsdEstimate);
        Assert.Equal(PrOutcomes.MergedUnchanged, entries[1].PrOutcome!.Outcome);
        Assert.Null(entries[0].PrOutcome);
    }

    [Fact]
    public async Task The_store_reads_an_empty_or_missing_file_a_junk_line_and_a_line_still_being_written()
    {
        using var stores = new Stores();
        Assert.Empty(await stores.Store.ReadAsync(default));
        await stores.Store.AppendAsync(Record("r1"), default);
        var path = Path.Combine(stores.Dir.Path, "learning.jsonl");
        File.AppendAllText(path, "not json\n{\"type\":\"task\",\"run_id\":\"half");

        Assert.Equal(["r1"], (await stores.Store.ReadAsync(default)).Select(e => e.Record.RunId));
    }

    // The outcome of a pull request

    private static PrSnapshot Snapshot(string head = "aaaa", bool merged = false, bool closed = false) =>
        new("node", 7, head, "main", false, merged, closed, "CLEAN", false, [], []);

    [Theory]
    [InlineData("aaaa", true, false, "aaaa", PrOutcomes.MergedUnchanged)]
    [InlineData("AAAA", true, false, "aaaa", PrOutcomes.MergedUnchanged)]
    [InlineData("bbbb", true, false, "aaaa", PrOutcomes.MergedEdited)]
    [InlineData("bbbb", true, false, null, PrOutcomes.Merged)]
    [InlineData("aaaa", false, true, "aaaa", PrOutcomes.Closed)]
    [InlineData("bbbb", false, false, "aaaa", PrOutcomes.Open)]
    public void A_pull_request_is_classified_by_its_head_at_the_merge(string head, bool merged, bool closed, string? pushed, string expected)
    {
        Assert.Equal(expected, PrOutcomes.Classify(Snapshot(head, merged, closed), pushed));
    }

    private sealed class Forge(FakeIntegrationHost host)
    {
        public List<RemoteRepository> Asked { get; } = [];

        public IIntegrationHost? For(RemoteRepository repository)
        {
            Asked.Add(repository);
            return host;
        }
    }

    [Fact]
    public async Task The_tracker_writes_an_outcome_only_when_it_changes_and_stops_reading_a_pull_request_once_it_is_merged()
    {
        using var stores = new Stores();
        var store = stores.Store;
        await store.AppendAsync(Record("r1", pr: Pr(7, "aaaa"), at: Now.AddHours(-1)), default);
        var host = new FakeIntegrationHost { Current = Snapshot() };
        var forge = new Forge(host);
        var tracker = new PrOutcomeTracker(store, forge.For, new FixedClock(Now));

        Assert.Equal(1, await tracker.RefreshAsync(14, default));
        Assert.Equal(0, await tracker.RefreshAsync(14, default));
        host.Current = Snapshot("aaaa", merged: true);
        Assert.Equal(1, await tracker.RefreshAsync(14, default));
        Assert.Equal(0, await tracker.RefreshAsync(14, default));

        Assert.Equal(3, host.Calls.Count(c => c == "get"));
        var entry = Assert.Single(await store.ReadAsync(default));
        Assert.Equal((PrOutcomes.MergedUnchanged, "aaaa"), (entry.PrOutcome!.Outcome, entry.PrOutcome.HeadSha));
    }

    [Fact]
    public async Task The_tracker_tells_a_merge_after_edits_and_a_closed_pull_request()
    {
        using var stores = new Stores();
        var store = stores.Store;
        await store.AppendAsync(Record("edited", pr: Pr(7, "aaaa")), default);
        await store.AppendAsync(Record("closed", pr: Pr(8, "cccc"), at: Now.AddHours(-2)), default);
        var host = new FakeIntegrationHost();
        host.Script = h => h.Calls.Count(c => c == "get") == 1 ? Snapshot("dddd", merged: true) : Snapshot("cccc", closed: true);

        await new PrOutcomeTracker(store, new Forge(host).For, new FixedClock(Now)).RefreshAsync(14, default);

        var byRun = (await store.ReadAsync(default)).ToDictionary(e => e.Record.RunId, e => e.PrOutcome!.Outcome);
        // The older record is read first (oldest first): "closed" got the merged snapshot with another head, "edited" the closed one.
        Assert.Equal(PrOutcomes.MergedEdited, byRun["closed"]);
        Assert.Equal(PrOutcomes.Closed, byRun["edited"]);
    }

    [Fact]
    public async Task The_tracker_leaves_alone_a_task_older_than_the_window_a_task_without_a_pull_request_and_a_forge_without_a_credential()
    {
        using var stores = new Stores();
        var store = stores.Store;
        await store.AppendAsync(Record("old", pr: Pr(), at: Now.AddDays(-15)), default);
        await store.AppendAsync(Record("none", pr: null), default);
        var host = new FakeIntegrationHost { Current = Snapshot() };
        Assert.Equal(0, await new PrOutcomeTracker(store, new Forge(host).For, new FixedClock(Now)).RefreshAsync(14, default));
        Assert.Empty(host.Calls);

        await store.AppendAsync(Record("fresh", pr: Pr()), default);
        Assert.Equal(0, await new PrOutcomeTracker(store, _ => null, new FixedClock(Now)).RefreshAsync(14, default));
        Assert.All(await store.ReadAsync(default), e => Assert.Null(e.PrOutcome));
    }

    [Fact]
    public async Task The_tracker_skips_a_pull_request_the_forge_cannot_answer_for_and_tries_again_next_time()
    {
        using var stores = new Stores();
        var store = stores.Store;
        await store.AppendAsync(Record("r1", pr: Pr()), default);
        var host = new FakeIntegrationHost { Current = Snapshot(merged: true), ReadFails = new IntegrationException("GitHub answered 502") };
        var tracker = new PrOutcomeTracker(store, new Forge(host).For, new FixedClock(Now));

        Assert.Equal(0, await tracker.RefreshAsync(14, default));
        host.ReadFails = null;
        Assert.Equal(1, await tracker.RefreshAsync(14, default));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task The_server_reads_outcomes_from_the_github_app_host_and_from_no_one_without_it()
    {
        using var stores = new Stores();
        var host = new FakeIntegrationHost { Current = Snapshot("head-1", merged: true) };
        await stores.Store.AppendAsync(Record("r1", pr: Pr(7, "head-1"), at: DateTimeOffset.UtcNow.AddMinutes(-5)), default);

        using (var off = new DrivenRunTests.Rig(learning: stores.Store))
        {
            Assert.Null(off.Run.ReadHost(new RemoteRepository("o", "r")));
            Assert.Equal(0, await off.Run.RefreshPrOutcomesAsync(default));
        }
        using var on = new DrivenRunTests.Rig(learning: stores.Store, integration: host);
        Assert.Same(host, on.Run.ReadHost(new RemoteRepository("o", "r")));
        Assert.Equal(1, await on.Run.RefreshPrOutcomesAsync(default));
        Assert.Equal(PrOutcomes.MergedUnchanged, Assert.Single(await stores.Store.ReadAsync(default)).PrOutcome!.Outcome);
    }

    [Fact]
    public void Another_forge_is_read_with_the_forge_token_else_the_push_credential_and_not_at_all_without_a_builder()
    {
        var host = new FakeIntegrationHost();
        List<string> tokens = [];
        var remote = new RemoteRepository("group", "project", ForgeKind.GitLab, "gitlab.example.test");
        using var withToken = new DrivenRunTests.Rig(forge: new DrivenForge("gitlab", TokenSecret: "forge-token"), forgeFor: (_, token) => { tokens.Add(token); return new GitLabFake(host); });
        using var withPush = new DrivenRunTests.Rig(forgeFor: (_, token) => { tokens.Add(token); return new GitLabFake(host); });
        using var without = new DrivenRunTests.Rig();

        Assert.NotNull(withToken.Run.ReadHost(remote));
        Assert.NotNull(withPush.Run.ReadHost(remote));
        Assert.Null(without.Run.ReadHost(remote));
        Assert.Equal(["value-of-forge-token", "value-of-secret-push"], tokens);
    }

    private sealed class GitLabFake(FakeIntegrationHost inner) : IForge
    {
        public Task<PullRequestRef> CreateDraftAsync(RemoteRepository repo, string head, string @base, string title, string body, CancellationToken ct) => throw new NotSupportedException();

        public Task EditAsync(RemoteRepository repo, int number, string title, string body, CancellationToken ct) => throw new NotSupportedException();

        public Task<PullRequestRef?> FindByHeadAsync(RemoteRepository repo, string head, CancellationToken ct) => throw new NotSupportedException();

        public Task<PrSnapshot> GetAsync(RemoteRepository repository, int number, CancellationToken ct) => inner.GetAsync(repository, number, ct);

        public Task<int> BehindByAsync(RemoteRepository repository, string baseBranch, string branch, CancellationToken ct) => inner.BehindByAsync(repository, baseBranch, branch, ct);

        public Task UpdateBranchAsync(RemoteRepository repository, int number, string expectedHeadSha, CancellationToken ct) => inner.UpdateBranchAsync(repository, number, expectedHeadSha, ct);

        public Task MarkReadyAsync(RemoteRepository repository, string nodeId, CancellationToken ct) => inner.MarkReadyAsync(repository, nodeId, ct);

        public Task EnableAutoMergeAsync(RemoteRepository repository, string nodeId, string headSha, CancellationToken ct) => inner.EnableAutoMergeAsync(repository, nodeId, headSha, ct);

        public Task MergeNowAsync(RemoteRepository repository, int number, string headSha, CancellationToken ct) => inner.MergeNowAsync(repository, number, headSha, ct);

        public Task CommentAsync(RemoteRepository repository, int number, string body, CancellationToken ct) => inner.CommentAsync(repository, number, body, ct);

        public Task<string> JobLogTailAsync(RemoteRepository repository, long jobId, int maxChars, CancellationToken ct) => inner.JobLogTailAsync(repository, jobId, maxChars, ct);
    }

    // The report

    private static LearningEntry Entry(LearningRecord record, string? prOutcome = null) =>
        new(record, prOutcome is null ? null : new PrOutcomeRecord(record.RunId, Now, prOutcome, null));

    [Theory]
    [InlineData(new int[0], 50, null)]
    [InlineData(new[] { 7 }, 80, 7)]
    [InlineData(new[] { 1, 2, 3, 4 }, 50, 2)]
    [InlineData(new[] { 1, 2, 3, 4 }, 80, 4)]
    [InlineData(new[] { 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 }, 50, 5)]
    [InlineData(new[] { 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 }, 80, 8)]
    [InlineData(new[] { 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 }, 100, 10)]
    public void Percentiles_are_nearest_rank(int[] values, int percent, int? expected)
    {
        Assert.Equal((decimal?)expected, LearningStats.Percentile(values.Select(v => (decimal)v), percent));
    }

    private static IReadOnlyList<LearningEntry> Synthetic()
    {
        List<LearningEntry> entries = [];
        // o/r M: ten tasks costing 1 to 10, seven completed, two partial, one failed; checkpoints on two completed and one partial; five pull requests.
        for (var i = 1; i <= 10; i++)
        {
            var outcome = i <= 7 ? LearningOutcome.Completed : i <= 9 ? LearningOutcome.Partial : LearningOutcome.Failed;
            var checkpoint = i is 2 or 3 or 8;
            entries.Add(Entry(Record($"m{i}", usd: i, outcome: outcome, checkpoint: checkpoint, pr: i <= 5 ? Pr(i) : null), i switch
            {
                1 => PrOutcomes.MergedUnchanged,
                2 or 3 => PrOutcomes.MergedEdited,
                4 => PrOutcomes.Closed,
                _ => null,
            }));
        }
        entries.Add(Entry(Record("s1", size: "S", usd: null)));
        entries.Add(Entry(Record("l1", repository: "a/b", size: "L", usd: 9m)));
        entries.Add(Entry(Record("old", usd: 100m, at: Now.AddDays(-100))));
        return entries;
    }

    [Fact]
    public void The_report_has_a_row_per_repository_and_size_class_with_the_rates_worked_out_from_the_records()
    {
        var report = LearningReport.Build(Synthetic());

        Assert.Equal(13, report.Tasks);
        Assert.Equal([("a/b", "L"), ("o/r", "S"), ("o/r", "M")], report.Groups.Select(g => (g.Repository, g.SizeClass)));
        var m = report.Groups[2];
        Assert.Equal(11, m.Tasks);
        // The old task is in the all-time report: the eleven spends are 1 to 10 and 100.
        Assert.Equal((11, 6m, 9m), (m.SpendKnown, m.SpendMedianUsd, m.SpendP80Usd));
    }

    [Fact]
    public void The_report_since_a_time_drops_older_tasks_and_the_numbers_follow()
    {
        var report = LearningReport.Build(Synthetic(), Now.AddDays(-30));

        Assert.Equal(12, report.Tasks);
        var m = report.Groups.Single(g => g is { Repository: "o/r", SizeClass: "M" });
        Assert.Equal((10, 10, 5m, 8m), (m.Tasks, m.SpendKnown, m.SpendMedianUsd, m.SpendP80Usd));
        Assert.Equal((7, 0.7, 2, 0.2), (m.Completed, m.CompletionRate, m.Partial, m.PartialRate));
        Assert.Equal((3, 0.3, 2, 0.667), (m.Checkpointed, m.CheckpointRate, m.CheckpointedCompleted, m.CheckpointedCompletedRate));
        Assert.Equal(new PrCounts(5, 1, 2, 0, 1, 1), m.Prs);
        Assert.Equal(0.25, m.MergedUnchangedRate);
    }

    [Fact]
    public void A_group_with_no_known_spend_no_checkpoint_and_no_pull_request_has_nulls_not_zeros()
    {
        var s = LearningReport.Build(Synthetic(), Now.AddDays(-30)).Groups.Single(g => g.SizeClass == "S");

        Assert.Equal((1, 0, null, null), (s.Tasks, s.SpendKnown, s.SpendMedianUsd, s.SpendP80Usd));
        Assert.Equal((0, null, null), (s.Checkpointed, s.CheckpointedCompletedRate, s.MergedUnchangedRate));
        Assert.Equal(new PrCounts(0, 0, 0, 0, 0, 0), s.Prs);
    }

    [Fact]
    public void An_empty_log_gives_an_empty_report()
    {
        var report = LearningReport.Build([]);

        Assert.Equal((0, 0), (report.Tasks, report.Groups.Count));
        Assert.Contains("0 tasks", LearningReport.Render(report));
    }

    [Fact]
    public void The_text_report_is_a_table_with_shares_and_their_counts()
    {
        var text = LearningReport.Render(LearningReport.Build(Synthetic(), Now.AddDays(-30)));

        Assert.Contains("| repository | size | tasks | spend median | spend p80 | completed | partial | checkpointed | completed after checkpoint | pr merged unchanged |", text);
        Assert.Contains("| o/r | M | 10 | 5.00 | 8.00 | 70% (7/10) | 20% (2/10) | 30% (3/10) | 67% (2/3) | 25% (1/4) |", text);
        Assert.Contains("| o/r | S | 1 | - | - | 100% (1/1) | 0% (0/1) | 0% (0/1) | - | - |", text);
        Assert.Contains("12 tasks since", text);
    }

    [Theory]
    [InlineData("30d", 30)]
    [InlineData("1d", 1)]
    public void Since_takes_a_number_of_days(string text, int days)
    {
        Assert.Equal(Now.AddDays(-days), LearningReport.ParseSince(text, Now));
    }

    [Fact]
    public void Since_takes_an_iso_time_and_nothing_else()
    {
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), LearningReport.ParseSince("2026-09-01T00:00:00Z", Now));
        Assert.Null(LearningReport.ParseSince("yesterday", Now));
        Assert.Null(LearningReport.ParseSince("d", Now));
    }

    [Fact]
    public async Task The_learn_report_command_prints_the_table_and_refuses_a_bad_since()
    {
        using var stores = new Stores();
        await stores.Store.AppendAsync(Record("r1", usd: 2m), default);
        StringWriter output = new(), error = new();

        Assert.Equal(0, await LearningCli.RunAsync(["--since", "30d"], stores.Store, output, error, Now, default));
        Assert.Contains("| o/r | M | 1 | 2.00 | 2.00 | 100% (1/1)", output.ToString());

        StringWriter badOut = new(), badErr = new();
        Assert.Equal(2, await LearningCli.RunAsync(["--since", "soon"], stores.Store, badOut, badErr, Now, default));
        Assert.Equal(2, await LearningCli.RunAsync(["--since"], stores.Store, badOut, badErr, Now, default));
        Assert.Equal(2, await LearningCli.RunAsync(["--all"], stores.Store, badOut, badErr, Now, default));
        Assert.Contains("usage: chargehand learn report", badErr.ToString());
        Assert.Equal("", badOut.ToString());
    }

    // GET /v1/learn

    [Fact]
    public async Task The_server_answers_the_report_as_json_filters_by_since_and_needs_the_key()
    {
        using var stores = new Stores();
        await stores.Store.AppendAsync(Record("fresh", usd: 2m, at: DateTimeOffset.UtcNow.AddHours(-1)), default);
        await stores.Store.AppendAsync(Record("stale", usd: 8m, at: DateTimeOffset.UtcNow.AddDays(-40)), default);
        DrivenRunTests.Rig? rig = null;
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), driven: (root, log) => (rig = new DrivenRunTests.Rig(dir: root, log: log, learning: stores.Store)).Run);
        using var _ = rig;

        var all = await s.Http.GetFromJsonAsync<JsonElement>("/v1/learn");
        Assert.Equal(2, all.GetProperty("tasks").GetInt32());
        var group = all.GetProperty("groups")[0];
        Assert.Equal(("o/r", "M", 2), (group.GetProperty("repository").GetString(), group.GetProperty("size_class").GetString(), group.GetProperty("tasks").GetInt32()));
        Assert.Equal(8m, group.GetProperty("spend_p80_usd").GetDecimal());

        var recent = await s.Http.GetFromJsonAsync<JsonElement>("/v1/learn?since=30d");
        Assert.Equal(1, recent.GetProperty("tasks").GetInt32());
        Assert.Equal(2m, recent.GetProperty("groups")[0].GetProperty("spend_p80_usd").GetDecimal());

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Http.GetAsync("/v1/learn?since=soon")).StatusCode);
        using var anonymous = new HttpClient { BaseAddress = s.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/learn")).StatusCode);
    }

    [Fact]
    public async Task An_accounts_key_sees_the_tasks_of_its_own_batches_only_and_the_owner_sees_all_including_those_from_before_accounts()
    {
        using var stores = new Stores();
        await stores.Store.AppendAsync(Record("a1", usd: 1m, at: DateTimeOffset.UtcNow.AddHours(-1)) with { Actor = "alice" }, default);
        await stores.Store.AppendAsync(Record("b1", usd: 2m, at: DateTimeOffset.UtcNow.AddHours(-1)) with { Actor = "bob" }, default);
        await stores.Store.AppendAsync(Record("o1", usd: 4m, at: DateTimeOffset.UtcNow.AddHours(-1)), default);
        DrivenRunTests.Rig? rig = null;
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [new AccountSettings("alice"), new AccountSettings("bob")],
            driven: (root, log) => (rig = new DrivenRunTests.Rig(dir: root, log: log, learning: stores.Store)).Run);
        using var _ = rig;
        using var alice = s.As(s.Keys.Create("alice", null).Key);

        var mine = await alice.GetFromJsonAsync<JsonElement>("/v1/learn");
        var all = await s.Http.GetFromJsonAsync<JsonElement>("/v1/learn");

        Assert.Equal((1, 1m), (mine.GetProperty("tasks").GetInt32(), mine.GetProperty("groups")[0].GetProperty("spend_median_usd").GetDecimal()));
        Assert.Equal(3, all.GetProperty("tasks").GetInt32());
    }

    [Fact]
    public async Task A_run_token_does_not_open_the_report()
    {
        using var stores = new Stores();
        DrivenRunTests.Rig? rig = null;
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), driven: (root, log) => (rig = new DrivenRunTests.Rig(dir: root, log: log, learning: stores.Store)).Run);
        using var _ = rig;
        var token = s.Tokens.Issue(new RunTokenClaims("run-x", "/srv/repo", Commit, null, null, DateTimeOffset.UtcNow.AddMinutes(10)), DateTimeOffset.UtcNow);
        using var scoped = new HttpClient { BaseAddress = s.BaseAddress };
        scoped.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        Assert.Equal(HttpStatusCode.Forbidden, (await scoped.GetAsync("/v1/learn")).StatusCode);
    }

    [Fact]
    public async Task A_server_with_driven_sessions_off_answers_an_empty_report()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));

        var body = await s.Http.GetFromJsonAsync<JsonElement>("/v1/learn");

        Assert.Equal(0, body.GetProperty("tasks").GetInt32());
        Assert.Equal(0, body.GetProperty("groups").GetArrayLength());
    }

    // The retro

    private sealed class FakeRetro(BatchRetro? answer = null, Exception? fail = null) : IBatchRetro
    {
        public List<RetroInput> Inputs { get; } = [];

        public Task<BatchRetro> WriteAsync(RetroInput input, CancellationToken ct)
        {
            Inputs.Add(input);
            return fail is null ? Task.FromResult(answer ?? new BatchRetro("written", null, "run-retro", input.Records.Count, "t1 cost 1.00 of 4.00.")) : throw fail;
        }
    }

    private sealed class FakeSink(Exception? fail = null) : IRetroSink
    {
        public List<(string Batch, BatchRetro Retro)> Got { get; } = [];

        public Task PublishAsync(string batchRunId, BatchRetro retro, CancellationToken ct)
        {
            Got.Add((batchRunId, retro));
            return fail is null ? Task.CompletedTask : throw fail;
        }
    }

    private static ResultContract Answer(ResultStatus status, string summary) =>
        new("result/v1", "run-retro", "answer", "trace", new PromptChain([], new AsSent("2.0.16", "build", "m", "2026-10-13")), status, summary, [], [], [], [], 0.9, new Usage(0, 0, 0, 0, 0),
            status == ResultStatus.Failed ? new ResultError(ErrorCode.CheckoutInvalid, "the checkout is invalid", false) : null);

    private static JsonElement? RetroArtifact(ResultContract result) =>
        result.Artifacts.SingleOrDefault(a => a.Kind == "retro") is { } artifact ? JsonDocument.Parse(artifact.Content!).RootElement : null;

    [Fact]
    public void The_retro_is_off_unless_the_profile_turns_it_on()
    {
        Assert.False(new DrivenLearning().Retro);
        using var dir = new TempDir();
        Assert.Null(Profile.Load(dir.Write("none.json", """{"schema":"profile/v1","driven":{}}""")).Driven!.Learning);
        Assert.Equal(new DrivenLearning(), Profile.Load(dir.Write("empty.json", """{"schema":"profile/v1","driven":{"learning":{}}}""")).Driven!.Learning);
        Assert.Equal(new DrivenLearning(true, "review", 7, 30),
            Profile.Load(dir.Write("on.json", """{"schema":"profile/v1","driven":{"learning":{"retro":true,"retro_preset":"review","pr_days":7,"pr_check_minutes":30}}}""")).Driven!.Learning);
    }

    [Theory]
    [InlineData("""{"retro_preset":" "}""")]
    [InlineData("""{"pr_days":0}""")]
    [InlineData("""{"pr_check_minutes":0}""")]
    public void A_learning_setting_that_cannot_work_is_refused_when_the_profile_loads(string learning)
    {
        using var dir = new TempDir();
        var path = dir.Write("bad.json", $$$"""{"schema":"profile/v1","driven":{"learning":{{{learning}}}}}""");

        Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ChargehandException>(() => Profile.Load(path)).Code);
    }

    [Theory]
    [InlineData("""{"schema":"profile/v1","driven":{"learning":{"retro":true,"retro_preset":"cheap","pr_days":14,"pr_check_minutes":60}}}""", true)]
    [InlineData("""{"schema":"profile/v1","driven":{"learning":{}}}""", true)]
    [InlineData("""{"schema":"profile/v1","driven":{"learning":{"pr_days":0}}}""", false)]
    [InlineData("""{"schema":"profile/v1","driven":{"learning":{"retro":"yes"}}}""", false)]
    [InlineData("""{"schema":"profile/v1","driven":{"learning":{"unknown":1}}}""", false)]
    public void The_schema_checks_driven_learning(string profile, bool valid)
    {
        using var doc = JsonDocument.Parse(profile);
        Assert.Equal(valid, ConfigFileTests.ProfileSchema.Evaluate(doc.RootElement).IsValid);
    }

    [Fact]
    public async Task With_the_retro_off_nothing_is_asked_and_the_batch_result_has_no_retro()
    {
        using var stores = new Stores();
        var retro = new FakeRetro();
        using var rig = new DrivenRunTests.Rig(learning: stores.Store, retro: retro, outcomes: (_, _) => Done(1m));

        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Empty(retro.Inputs);
        Assert.Null(RetroArtifact(result));
        Assert.Equal(ResultStatus.Completed, result.Status);
    }

    [Fact]
    public async Task With_the_retro_on_it_is_given_only_this_batchs_records_and_lands_on_the_batch_result_and_the_sink()
    {
        using var stores = new Stores();
        await stores.Store.AppendAsync(Record("other") with { BatchId = "run-another-batch" }, default);
        var (retro, sink) = (new FakeRetro(), new FakeSink());
        using var rig = new DrivenRunTests.Rig(learning: stores.Store, learningSettings: new DrivenLearning(Retro: true), retro: retro, retroSink: sink, outcomes: (_, _) => Done(1m));

        var result = await rig.Run.RunAsync(Request(new DrivenTask("t1", Goal: "one"), new DrivenTask("t2", Goal: "two")), BatchId, Token, default);

        var input = Assert.Single(retro.Inputs);
        Assert.Equal(BatchId, input.RunId);
        Assert.Equal(["t1", "t2"], input.Records.Select(r => r.TaskId).Order());
        var artifact = RetroArtifact(result)!.Value;
        Assert.Equal(("written", "run-retro", 2, "t1 cost 1.00 of 4.00."), (artifact.GetProperty("status").GetString(), artifact.GetProperty("run_id").GetString(), artifact.GetProperty("tasks").GetInt32(), artifact.GetProperty("text").GetString()));
        Assert.Equal(BatchId, Assert.Single(sink.Got).Batch);
        Assert.True(sink.Got[0].Retro.Written);
    }

    [Fact]
    public async Task A_retro_that_fails_or_cannot_run_is_reported_skipped_and_never_changes_the_batch()
    {
        using var stores = new Stores();
        using var failing = new DrivenRunTests.Rig(learning: stores.Store, learningSettings: new DrivenLearning(Retro: true), retro: new FakeRetro(fail: new RetroException("the retro run printed nothing")), outcomes: (_, _) => Done(1m));
        var failed = await failing.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Equal(ResultStatus.Completed, failed.Status);
        Assert.Equal(("skipped", "the retro run printed nothing"), (RetroArtifact(failed)!.Value.GetProperty("status").GetString(), RetroArtifact(failed)!.Value.GetProperty("reason").GetString()));

        using var none = new DrivenRunTests.Rig(learning: stores.Store, learningSettings: new DrivenLearning(Retro: true), outcomes: (_, _) => Done(1m));
        var unwritten = await none.Run.RunAsync(One(), "run-batch-l2", Token, default);

        Assert.Equal(ResultStatus.Completed, unwritten.Status);
        Assert.Equal("skipped", RetroArtifact(unwritten)!.Value.GetProperty("status").GetString());
        Assert.Contains("no retro writer", RetroArtifact(unwritten)!.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_retro_with_no_records_to_read_is_skipped_and_a_sink_that_throws_is_ignored()
    {
        using var noStore = new DrivenRunTests.Rig(learningSettings: new DrivenLearning(Retro: true), retro: new FakeRetro(), outcomes: (_, _) => Done(1m));
        var skipped = await noStore.Run.RunAsync(One(), BatchId, Token, default);
        Assert.Equal("skipped", RetroArtifact(skipped)!.Value.GetProperty("status").GetString());

        using var stores = new Stores();
        using var rig = new DrivenRunTests.Rig(learning: stores.Store, learningSettings: new DrivenLearning(Retro: true), retro: new FakeRetro(), retroSink: new FakeSink(new InvalidOperationException("inbox is full")), outcomes: (_, _) => Done(1m));
        var written = await rig.Run.RunAsync(One(), BatchId, Token, default);
        Assert.Equal(ResultStatus.Completed, written.Status);
        Assert.Equal("written", RetroArtifact(written)!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_model_retro_asks_a_read_only_run_with_the_records_and_no_goal_and_cuts_a_long_answer()
    {
        List<(RunRequest Request, string RunId, string Parent)> asked = [];
        var retro = new ModelRetro((request, runId, parent, ct) =>
        {
            asked.Add((request, runId, parent));
            return Task.FromResult(Answer(ResultStatus.Completed, new string('x', ModelRetro.MaxTextLength + 50)));
        }, "cheap");
        var records = new[] { Record("r1", usd: 2m, outcome: LearningOutcome.Partial, checkpoint: true) };

        var written = await retro.WriteAsync(new RetroInput(One(), BatchId, records), default);

        var (request, runId, parent) = Assert.Single(asked);
        Assert.Equal(("cheap", BatchId, runId), (request.Context.Preset, parent, written.RunId));
        Assert.Equal("/srv/repo", request.Context.Repository!.Path);
        Assert.Contains("\"id\":\"task-r1\"", request.Text);
        Assert.Contains("\"outcome\":\"partial\"", request.Text);
        Assert.Contains("\"governor\":[{\"signal\":\"spend\",\"action\":\"checkpoint\",\"count\":1,\"acted\":0}]", request.Text);
        Assert.Contains("do not read the repository and do not change any file", request.Text);
        Assert.EndsWith(" [cut]", written.Text);
        Assert.Equal(ModelRetro.MaxTextLength + " [cut]".Length, written.Text!.Length);
    }

    [Theory]
    [InlineData(ResultStatus.Failed, "something")]
    [InlineData(ResultStatus.Completed, "   ")]
    public async Task The_model_retro_refuses_a_run_that_did_not_complete_or_printed_nothing(ResultStatus status, string summary)
    {
        var retro = new ModelRetro((_, _, _, _) => Task.FromResult(Answer(status, summary)), "cheap");

        await Assert.ThrowsAsync<RetroException>(() => retro.WriteAsync(new RetroInput(One(), BatchId, [Record()]), default));
    }
}
