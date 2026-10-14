using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Kernel;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>CHARGEHAND-214: a driven batch leaves the records the operator console reads: its events on disk, and for each task every session, the governor's whole log and the pull request's checks.
/// The sessions are scripted by the fake output volume; nothing starts a container.</summary>
public class DrivenRecordsTests
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";
    private const string BatchId = "run-batch-r";

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private static RunRequest Request(params string[] goals) =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([.. goals.Select((g, i) => new DrivenTask($"t{i + 1}", Goal: g))], MaxParallel: 1, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    private static SessionOutcome Stopped(string reason) =>
        new(SessionStatus.Stalled, reason, 80, 1000, 200, 4000, null, 0, [], true, Commits: 3);

    private static SessionOutcome Done() =>
        new(SessionStatus.Completed, "", 30, 500, 100, 2000, 0.4m, 0, [], true, CacheWriteTokens: 50, UsdEstimate: 1.25m, Commits: 4);

    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static string GovernorLines(int count, string signal = "repeated_call") =>
        string.Join('\n', Enumerable.Range(0, count).Select(i => JsonSerializer.Serialize(
            new GovernorDecision(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero).AddSeconds(i), i + 1, GovernorMode.Observe, GovernorAction.Warn, signal, "first_hit",
                new Dictionary<string, string> { ["calls"] = "4" }, 0.5m, 4m, "M", false, new BudgetSource("flat")), Snake)));

    [Fact]
    public async Task A_batch_keeps_its_events_on_disk_without_its_result_and_they_are_there_for_a_new_process()
    {
        using var rig = new DrivenRunTests.Rig(durable: true, records: true);
        await rig.Run.RunAsync(Request("Add a retry", "Add a backoff"), BatchId, Token, default);
        rig.Restart();

        var events = new BatchRecords(rig.Records!.Root).ReadEvents(BatchId)!;

        Assert.Equal(RunEventKind.Started, events[0].Event);
        Assert.Equal(RunEventKind.RunFinished, events[^1].Event);
        Assert.Null(events[^1].Result);
        Assert.Contains(events, e => e.Event == RunEventKind.ContainerStarted && e.TaskId == "t1");
        Assert.Contains(events, e => e.Event == RunEventKind.PrOpened && e.TaskId == "t2");
        Assert.Equal(2, events.Count(e => e.Event == RunEventKind.TaskFinished));
    }

    [Fact]
    public async Task A_batch_with_no_records_directory_writes_nothing_and_runs_as_before()
    {
        using var rig = new DrivenRunTests.Rig(durable: true);

        var result = await rig.Run.RunAsync(Request("Add a retry"), BatchId, Token, default);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Null(rig.Records);
        Assert.False(Directory.Exists(Path.Combine(rig.Dir.Path, "records")));
    }

    [Fact]
    public async Task Every_session_of_a_task_is_recorded_with_its_role_why_it_ended_its_tokens_and_cost()
    {
        using var rig = new DrivenRunTests.Rig(records: true, outcomes: (_, goal) => goal.Contains("Continuation ", StringComparison.Ordinal) ? Done() : Stopped("turn_limit"));

        await rig.Run.RunAsync(Request("one"), BatchId, Token, default);

        var sessions = rig.Records!.ReadTasks(BatchId).Sessions;
        Assert.Equal(rig.Engine.Started.Select(s => s.RunId), sessions.Select(s => s.RunId));
        Assert.Equal(["first", "continuation"], sessions.Select(s => s.Role));
        Assert.All(sessions, s => Assert.Equal("t1", s.TaskId));
        Assert.Equal(("stalled", "turn_limit", 80, 1000L, 200L, 4000L), (sessions[0].Status, sessions[0].Reason, sessions[0].Turns, sessions[0].InputTokens, sessions[0].OutputTokens, sessions[0].CacheReadTokens));
        Assert.Equal(("completed", null, 1.25m, 50L), (sessions[1].Status, sessions[1].Reason, sessions[1].UsdEstimate, sessions[1].CacheWriteTokens));
        Assert.True(sessions[0].Ended >= sessions[0].Started);
        Assert.True(sessions[1].Started >= sessions[0].Ended);
        // A subscription has no price: the estimate is there and the priced cost is not.
        Assert.All(sessions, s => Assert.Null(s.Usd));
        Assert.Equal(4, sessions[1].Commits);
    }

    [Fact]
    public async Task On_an_api_key_the_priced_cost_of_a_session_is_recorded()
    {
        using var rig = new DrivenRunTests.Rig(records: true, apiKey: true, outcomes: (_, _) => Done());

        await rig.Run.RunAsync(Request("one"), BatchId, Token, default);

        Assert.Equal(0.4m, Assert.Single(rig.Records!.ReadTasks(BatchId).Sessions).Usd);
    }

    [Fact]
    public async Task The_governor_log_is_recorded_in_full_where_the_outcome_file_has_only_the_first_50()
    {
        var volumes = new DrivenFakes.FakeVolumes(_ => SessionStatus.Completed) { GovernorLog = (_, _) => GovernorLines(120) };
        using var rig = new DrivenRunTests.Rig(records: true, volumes: volumes);

        await rig.Run.RunAsync(Request("one"), BatchId, Token, default);

        var log = Assert.Single(rig.Records!.ReadTasks(BatchId).Governor);
        Assert.Equal((120, 120), (log.Total, log.Decisions.Count));
        Assert.Equal(("repeated_call", GovernorAction.Warn, 1, 4m), (log.Decisions[0].Signal, log.Decisions[0].Action, log.Decisions[0].Turn, log.Decisions[0].BudgetUsd));
        Assert.Equal("4", log.Decisions[0].Values["calls"]);
        Assert.Equal(log.RunId, rig.Engine.Started[0].RunId);
    }

    [Fact]
    public async Task A_log_longer_than_the_cap_keeps_the_first_decisions_and_the_count()
    {
        var volumes = new DrivenFakes.FakeVolumes(_ => SessionStatus.Completed) { GovernorLog = (_, _) => GovernorLines(BatchRecords.MaxDecisionsPerSession + 50) };
        using var rig = new DrivenRunTests.Rig(records: true, volumes: volumes);

        await rig.Run.RunAsync(Request("one"), BatchId, Token, default);

        var log = Assert.Single(rig.Records!.ReadTasks(BatchId).Governor);
        Assert.Equal((BatchRecords.MaxDecisionsPerSession + 50, BatchRecords.MaxDecisionsPerSession), (log.Total, log.Decisions.Count));
        Assert.Equal(BatchRecords.MaxDecisionsPerSession, log.Decisions[^1].Turn);
    }

    [Fact]
    public async Task A_runner_too_old_to_serve_the_log_leaves_the_outcome_files_first_decisions_in_its_place()
    {
        var decision = new GovernorDecision(DateTimeOffset.UtcNow, 3, GovernorMode.Observe, GovernorAction.Checkpoint, "spend", "spend_checkpoint", new Dictionary<string, string>(), 3.4m, 4m, "M");
        using var rig = new DrivenRunTests.Rig(records: true, outcomes: (_, _) => Done() with { Governor = [decision] });

        await rig.Run.RunAsync(Request("one"), BatchId, Token, default);

        var log = Assert.Single(rig.Records!.ReadTasks(BatchId).Governor);
        Assert.Equal(("spend", GovernorAction.Checkpoint, 1), (log.Decisions[0].Signal, log.Decisions[0].Action, log.Total));
    }

    [Fact]
    public async Task A_task_with_no_governor_decisions_records_no_governor_line()
    {
        using var rig = new DrivenRunTests.Rig(records: true);

        await rig.Run.RunAsync(Request("one"), BatchId, Token, default);

        Assert.Empty(rig.Records!.ReadTasks(BatchId).Governor);
    }

    [Fact]
    public async Task A_cancel_records_the_sessions_it_cut_short_as_cancelled_with_no_numbers()
    {
        using var rig = new DrivenRunTests.Rig(records: true);
        rig.Engine.HoldRunning = true;
        using var cts = new CancellationTokenSource();
        var running = rig.Run.RunAsync(Request("Add a retry", "Add a backoff"), BatchId, Token, cts.Token, cancelledByCaller: () => true);
        while (rig.Engine.Started.Count < 1)
            await Task.Delay(5);
        await cts.CancelAsync();
        await running;

        var sessions = rig.Records!.ReadTasks(BatchId).Sessions;

        var cut = Assert.Single(sessions, s => s.TaskId == "t1");
        Assert.Equal(("cancelled", null, "first", 0L), (cut.Status, cut.Reason, cut.Role, cut.InputTokens));
        Assert.Equal(rig.Engine.Started[0].RunId, cut.RunId);
    }

    [Fact]
    public async Task A_server_that_is_stopping_records_no_session_for_a_task_the_next_process_takes_up()
    {
        using var rig = new DrivenRunTests.Rig(records: true, durable: true);
        rig.Engine.HoldRunning = true;
        using var cts = new CancellationTokenSource();
        var running = rig.Run.RunAsync(Request("Add a retry"), BatchId, Token, cts.Token);
        while (rig.Engine.Started.Count < 1)
            await Task.Delay(5);
        await cts.CancelAsync();
        await running;

        Assert.Empty(rig.Records!.ReadTasks(BatchId).Sessions);
    }

    [Fact]
    public async Task A_session_that_could_not_start_is_recorded_as_an_error_with_the_reason()
    {
        using var rig = new DrivenRunTests.Rig(records: true);
        rig.Engine.OnStart = _ => new ChargehandException(ErrorCode.ContainerUnavailable, "the runner is full", "retry later");

        await rig.Run.RunAsync(Request("one"), BatchId, Token, default);

        var failed = Assert.Single(rig.Records!.ReadTasks(BatchId).Sessions);
        Assert.Equal(("error", "the runner is full"), (failed.Status, failed.Reason));
    }

    [Fact]
    public async Task The_checks_the_integrator_saw_and_each_ci_fix_attempt_and_its_session_are_recorded_for_the_task()
    {
        var host = new FakeIntegrationHost();
        host.Logs[900] = "FAILED Retry.Tests: expected 3 but got 2";
        using var rig = new DrivenRunTests.Rig(records: true, integrator: new DrivenIntegrator(1, 2, "app-key", new Dictionary<string, string> { ["o/r"] = "merge" }), integration: host,
            outcomes: (_, goal) => goal.StartsWith("Fix the failing CI checks", StringComparison.Ordinal)
                ? new SessionOutcome(SessionStatus.Completed, "", 10, 300, 50, 900, null, 0, [], true, Commits: 5)
                : new SessionOutcome(SessionStatus.Completed, "", 30, 1000, 200, 4000, null, 0, [], true, Commits: 4));
        host.Script = h =>
        {
            var fixedOnce = rig.Handover.Inputs.Count >= 2;
            return h.Current with { HeadSha = fixedOnce ? "head1" : "head0", Merged = fixedOnce, Checks = [new CheckStatus("build", true, fixedOnce ? CheckOutcome.Passed : CheckOutcome.Failed, 900), new CheckStatus("lint", false, CheckOutcome.Passed, 901)] };
        };

        await rig.Run.RunAsync(Request("Add a retry"), BatchId, Token, default);

        var records = rig.Records!.ReadTasks(BatchId);
        Assert.Equal(["first", "ci_fix"], records.Sessions.Select(s => s.Role));
        Assert.Equal(rig.Engine.Started.Select(s => s.RunId), records.Sessions.Select(s => s.RunId));
        var attempt = Assert.Single(records.Attempts);
        Assert.Equal(("t1", 1, 3, true), (attempt.TaskId, attempt.Attempt, attempt.MaxAttempts, attempt.Pushed));
        Assert.Equal(["build"], attempt.Failing);
        Assert.Equal(CiFixPolicy.JobsKey(["build"]), attempt.JobsKey);
        // A look is kept when it differs from the one before: the failing head, then the fixed one.
        Assert.Equal(["head0", "head1"], records.Checks.Select(c => c.HeadSha).Distinct());
        var failing = records.Checks[0];
        Assert.Equal(("fix", 1), (failing.Decision, failing.Pull));
        Assert.Equal("merged", records.Checks[^1].Decision);
        Assert.Equal([("build", true, "failed", 900L), ("lint", false, "passed", 901L)], failing.Checks.Select(c => (c.Name, c.Required, c.Outcome, c.JobId)));
        Assert.Equal(records.Checks.Count, records.Checks.Select(c => $"{c.HeadSha}|{c.MergeStatus}|{c.Decision}|{string.Join(',', c.Checks.Select(k => k.Outcome))}").Distinct().Count());
    }
}
