using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>C4 slice S1: how a batch behaves across a restart today, pinned before the batch, task and session state machines replace the code (C4 brief, section 3). "Restart" is a
/// second <see cref="DrivenRun"/> on the same run log, state directory and fake engine, as in <see cref="DrivenResumeTests"/>. The skipped tests state what the owner decided C4 makes durable.</summary>
public class DrivenRestartCharacterizationTests
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";
    private const string BatchId = "run-batch-c";
    private const string Durability = "C4 durability slice: owner decision 2026-10-14";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private static RunRequest Request(params string[] goals) =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([.. goals.Select((g, i) => new DrivenTask($"t{i + 1}", Goal: g))], MaxParallel: 1, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    private static List<(string Id, string Status, string? Detail, string? Error)> Rows(ResultContract result) =>
        [.. JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "driven-batch").Content!).RootElement.GetProperty("tasks").EnumerateArray()
            .Select(t => (t.GetProperty("id").GetString()!, t.GetProperty("status").GetString()!, Text(t, "detail"), Text(t, "error_code")))];

    private static string? Text(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DrivenIntegrator Merge() => new(1, 2, "app-key", new Dictionary<string, string> { ["o/r"] = "merge" });

    [Fact]
    public async Task A_task_restarted_after_its_session_ended_records_that_session_a_second_time()
    {
        using var rig = new DrivenRunTests.Rig(durable: true, records: true);
        using var stop = new CancellationTokenSource();
        // The server stops inside the handover: the session's outcome was read, recorded and saved as Finished.
        rig.Handover.OnPushed = n =>
        {
            if (n != 1)
                return;
            stop.Cancel();
            throw new OperationCanceledException(stop.Token);
        };
        await rig.Run.RunAsync(Request("one"), BatchId, Token, stop.Token, null, () => false);
        rig.Handover.OnPushed = null;
        var state = Assert.Single(await rig.Restart().UnfinishedAsync(default));
        var entry = state.Tasks.Single();
        Assert.Equal(TaskPhase.Finished, entry.Phase);
        Assert.Single(rig.Records!.ReadTasks(BatchId).Sessions);

        var result = await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        Assert.Equal(ResultStatus.Completed, result.Status);
        // Re-attached, not started again; the handover runs again.
        Assert.Single(rig.Engine.Started);
        Assert.Equal(2, rig.Handover.Inputs.Count);
        // pins current behaviour; C4 decision pending: the re-attach reads the outcome again and records the one session twice (C4 brief, section 3, item 6).
        var sessions = rig.Records.ReadTasks(BatchId).Sessions;
        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, s => Assert.Equal((entry.RunId, "first"), (s.RunId, s.Role)));
    }

    /// <summary>Reports a tally past its cap and then returns its own outcome, as a task runner that finished does.</summary>
    private sealed class OverCapRunner : ITaskRunner
    {
        public TaskOutcome? Returned { get; private set; }

        public Task<TaskOutcome> RunAsync(ResolvedTask task, IProgress<TaskUsage> progress, CancellationToken ct)
        {
            progress.Report(new TaskUsage(5000, 0));
            Returned = new TaskOutcome(task.Id, TaskState.Failed, "run-t1", "the tests failed", ErrorCode.VerificationFailed, 0, 5000);
            return Task.FromResult(Returned);
        }
    }

    [Fact]
    public async Task The_scheduler_saves_a_cap_rewrite_of_the_outcome_the_task_runner_returned()
    {
        var runner = new OverCapRunner();
        var finished = new List<TaskOutcome>();

        await new BatchScheduler(runner).RunAsync([new ResolvedTask("t1", null, "goal")], new BatchLimits(1, null, null, new TaskLimits(1000, null)), default, onFinished: finished.Add);

        // The task runner's save (its own outcome) is followed by the scheduler's (the rewrite): two writers of one end.
        Assert.Equal((TaskState.Failed, ErrorCode.VerificationFailed, "the tests failed"), (runner.Returned!.State, runner.Returned.Error, runner.Returned.Detail));
        var saved = Assert.Single(finished);
        Assert.Equal((TaskState.Failed, ErrorCode.CostCapReached, "stopped: the task's cap was exceeded"), (saved.State, saved.Error, saved.Detail));
    }

    [Fact]
    public async Task A_restart_between_the_task_runners_save_and_the_cap_rewrite_keeps_the_task_runners_outcome()
    {
        using var rig = new DrivenRunTests.Rig(durable: true);
        // What the snapshot holds when the server stops after the task runner saved its outcome and before the scheduler's rewrite was saved (onFinished skips the save while detaching).
        var own = new TaskOutcome("t1", TaskState.Failed, "run-t1", "the tests failed", ErrorCode.VerificationFailed, 0, 5000);
        new BatchStateStore(rig.StateDirectory).Save(new BatchState(BatchId, DateTimeOffset.UtcNow, "t", Request("one", "two"), null,
            [new TaskEntry("t1", null, "one", TaskPhase.Failed, "run-t1", Outcome: own), new TaskEntry("t2", null, "two")], []));
        var state = Assert.Single(await rig.Restart().UnfinishedAsync(default));

        var result = await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        // pins current behaviour; C4 decision pending: the new process's scheduler never saw the cap, so the outcome is not rewritten (C4 brief, section 3, item 1).
        var rows = Rows(result);
        Assert.Equal(("t1", "failed", "verification_failed"), (rows[0].Id, rows[0].Status, rows[0].Error));
        Assert.Contains("the tests failed", rows[0].Detail, StringComparison.Ordinal);
        Assert.Equal(("t2", "completed"), (rows[1].Id, rows[1].Status));
        Assert.Single(rig.Engine.Started);
    }

    [Fact]
    public async Task A_follow_up_cut_by_a_restart_is_dropped_and_the_task_stays_completed()
    {
        using var stop = new CancellationTokenSource();
        var host = new FakeIntegrationHost();
        // The server stops at the integrator's first read, before the draft is marked ready.
        host.Script = h =>
        {
            if (h.Calls.Count == 1)
            {
                stop.Cancel();
                throw new OperationCanceledException(stop.Token);
            }
            return h.Current;
        };
        using var rig = new DrivenRunTests.Rig(durable: true, integrator: Merge(), integration: host);
        await rig.Run.RunAsync(Request("one"), BatchId, Token, stop.Token, null, () => false);
        host.Script = null;
        var state = Assert.Single(await rig.Restart().UnfinishedAsync(default));
        var saved = state.Tasks.Single();
        Assert.Equal((TaskPhase.HandedOver, TaskState.Completed), (saved.Phase, saved.Outcome!.State));

        var result = await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        // pins current behaviour; C4 decision pending: the resumed task returns its saved outcome, so ready, merge and CI fixes never run (C4 brief, section 3, item 3).
        Assert.Equal(["get"], host.Calls);
        Assert.Single(rig.Engine.Started);
        Assert.Single(rig.Handover.Inputs);
        var row = Assert.Single(Rows(result));
        Assert.Equal(("t1", "completed"), (row.Id, row.Status));
        Assert.Contains("integration ended early: cancelled", row.Detail, StringComparison.Ordinal);
    }

    [Fact(Skip = Durability)]
    public async Task A_rate_limit_pause_survives_a_restart_and_holds_new_starts_until_it_lifts()
    {
        using var rig = new DrivenRunTests.Rig(durable: true, script: goal => goal == "two" ? SessionStatus.RateLimited : SessionStatus.Completed);
        using var stop = new CancellationTokenSource();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = rig.Run.RunAsync(Request("one", "two", "three"), BatchId, Token, stop.Token, e =>
        {
            if (e.Detail?.Contains("held until", StringComparison.Ordinal) == true)
                held.TrySetResult();
        }, () => false);
        await held.Task.WaitAsync(Patience);
        var pause = Assert.Single(rig.Run.RateLimitPauses(DateTimeOffset.UtcNow));
        await stop.CancelAsync();
        await first;
        var state = Assert.Single(await rig.Restart().UnfinishedAsync(default));

        using var cancel = new CancellationTokenSource();
        var heldAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = rig.Run.RunAsync(state.Request, state.RunId, Token, cancel.Token, e =>
        {
            if (e.Detail?.Contains("held until", StringComparison.Ordinal) == true)
                heldAgain.TrySetResult();
        }, () => true, state);
        await heldAgain.Task.WaitAsync(Patience);

        // The resumed batch keeps the pause it had: t3 does not start before the limit lifts.
        var kept = Assert.Single(rig.Run.RateLimitPauses(DateTimeOffset.UtcNow));
        Assert.Equal(("t2", pause.ResetsAt), (kept.TaskId, kept.ResetsAt));
        Assert.Equal(2, rig.Engine.Started.Count);
        await cancel.CancelAsync();
        await resumed;
    }

    [Fact(Skip = Durability)]
    public async Task An_operator_cancel_before_start_survives_a_restart_and_the_task_never_starts()
    {
        using var rig = new DrivenRunTests.Rig(durable: true);
        rig.Engine.HoldNth = 1;
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Engine.OnStart = _ =>
        {
            started.TrySetResult();
            return null;
        };
        var first = rig.Run.RunAsync(Request("one", "two"), BatchId, Token, stop.Token, null, () => false);
        await started.Task.WaitAsync(Patience);
        // The operator cancels t2 while t1 runs, and the server stops before the scheduler looks again.
        Assert.True(rig.Run.ControlOf(BatchId)!.CancelTask("t2", _ => true));
        await stop.CancelAsync();
        await first;
        var state = Assert.Single(await rig.Restart().UnfinishedAsync(default));
        rig.Engine.OnStart = null;
        rig.Engine.Release();

        var result = await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        Assert.Single(rig.Engine.Started);
        var rows = Rows(result);
        Assert.Equal(("t1", "completed"), (rows[0].Id, rows[0].Status));
        Assert.Equal(("t2", "cancelled"), (rows[1].Id, rows[1].Status));
    }
}
