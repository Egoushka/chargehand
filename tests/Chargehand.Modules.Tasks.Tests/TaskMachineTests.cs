using Chargehand.Contracts;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;
using Chargehand.Modules.Tasks.Core;

namespace Chargehand.Modules.Tasks.Tests;

/// <summary>C4 slice S3: a task entry's transitions as a table, one row per event. Each row is the entry before, the event, the entry after and the commands. No fakes, no clock, no file system.</summary>
public class TaskMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly SessionOutcome Spent = new(SessionStatus.Stalled, "max_turns", 80, 1000, 200, 4000, null, 0, [], true, Commits: 3);
    private static readonly BestBundle Best = new("run-1", 3);
    private static readonly TaskOutcome Completed = new("t1", TaskState.Completed, "run-1", "pull request opened");

    /// <summary>Planned, placed, and with a finished first session: what the events below change from.</summary>
    private static readonly TaskEntry Planned = new("t1", "ref-1", "goal", From: new ContinuedFrom("run-0", null), DependsOn: ["t0"], Size: "S", SplitFrom: "t", SizeFrom: "plan", Title: "Title");

    private static readonly TaskEntry Begun = Planned with
    {
        Phase = TaskPhase.Started,
        RunId = "run-1",
        Branch = "chargehand/run-1",
        Started = T0,
        Runs = ["run-1"],
        SessionStarted = T0,
        Runner = "r1",
    };

    private static readonly TaskEntry Running = Begun with { Container = "chargehand-run-1" };

    private static readonly TaskEntry Read = Running with { Phase = TaskPhase.Finished, Best = Best };

    private static readonly TaskEntry Continuing = Read with
    {
        Phase = TaskPhase.Started,
        RunId = "run-2",
        Container = null,
        Continuation = 1,
        Prior = Spent,
        Runs = ["run-1", "run-2"],
        SessionStarted = T0.AddHours(1),
        SessionGoal = "goal again",
        StartDetail = "continuation 1",
    };

    private sealed record Row(TaskEntry Before, TaskEvent Event, TaskEntry After);

    private static readonly Dictionary<string, Row> Rows = new()
    {
        ["placed: the runner is kept, nothing else changes"] = new(Planned, new TaskPlaced("r1"), Planned with { Runner = "r1" }),
        ["began: pending to started with run id, branch and times"] = new(Planned with { Runner = "r1" }, new TaskBegan("run-1", "chargehand/run-1", T0, T0, null), Begun),
        ["began again: an earlier attempt's session fields are cleared"] = new(Continuing with { Container = "c", Phase = TaskPhase.Finished }, new TaskBegan("run-1", "chargehand/run-1", T0, T0, Best),
            Begun with { Best = Best }),
        ["renamed: a session that never started gets a new run id and no container"] = new(Running with { Runs = ["run-1"] }, new TaskRunRenamed("run-9", ["run-9"]),
            Begun with { RunId = "run-9", Runs = ["run-9"] }),
        ["session starting: its start time"] = new(Begun, new TaskSessionStarting(T0.AddMinutes(1)), Begun with { SessionStarted = T0.AddMinutes(1) }),
        ["container started: its name"] = new(Begun, new TaskContainerStarted("chargehand-run-1"), Running),
        ["session read: started to finished"] = new(Running with { Best = Best }, new TaskSessionRead(), Read),
        ["continuation began: finished to started on the next run id"] = new(Read, new TaskContinuationBegan("run-2", ["run-1", "run-2"], 1, Spent, T0.AddHours(1), Best, "goal again", "continuation 1"),
            Continuing),
        ["continuation failed: back to finished on the previous session"] = new(Continuing with { Container = "chargehand-run-2" }, new TaskContinuationFailed("run-1", ["run-1"], 0),
            Continuing with { Phase = TaskPhase.Finished, RunId = "run-1", Continuation = 0, Runs = ["run-1"] }),
        ["finished completed: handed over with its outcome"] = new(Read, new TaskFinished(Completed), Read with { Phase = TaskPhase.HandedOver, Outcome = Completed }),
        // pins current behaviour; C4 decision pending: Failed stands for failed, cancelled, needs input and not started alike (C4 brief, section 3, item 2).
        ["finished failed: failed"] = Ended(TaskState.Failed),
        ["finished cancelled: failed"] = Ended(TaskState.Cancelled),
        ["finished needing input: failed, even with a partial pull request"] = Ended(TaskState.NeedsInput),
        ["finished not started: failed"] = Ended(TaskState.NotStarted),
        // pins current behaviour; C4 decision pending: the scheduler's cap rewrite is a second end that overwrites the first (item 1).
        ["finished again: the later outcome stands"] = new(Read with { Phase = TaskPhase.HandedOver, Outcome = Completed }, new TaskFinished(Completed with { Detail = "rewritten" }),
            Read with { Phase = TaskPhase.HandedOver, Outcome = Completed with { Detail = "rewritten" } }),
        ["replaced: back to pending, keeping what planning gave it"] = new(Continuing with { Outcome = Completed }, new TaskReplaced(), Planned),
    };

    private static Row Ended(TaskState state)
    {
        var outcome = new TaskOutcome("t1", state, "run-1", "ended");
        return new(Read, new TaskFinished(outcome), Read with { Phase = TaskPhase.Failed, Outcome = outcome });
    }

    public static TheoryData<string> Names() => new(Rows.Keys);

    [Theory]
    [MemberData(nameof(Names))]
    public void Decides(string row)
    {
        var (before, input, after) = Rows[row];

        var (state, commands) = TaskMachine.Decide(before, input);

        Assert.Equal(Shown(after), Shown(state));
        Assert.Equal(new JournalTaskEvent(input), commands[0]);
        Assert.Equal(input is TaskFinished f ? [new ReportTaskEnd(f.Outcome)] : [], commands.Skip(1));
    }

    /// <summary>The entry with its lists as text: record equality compares lists by reference.</summary>
    private static string Shown(TaskEntry e) =>
        (e with { Runs = null, DependsOn = null }) + $" Runs [{string.Join(",", e.Runs ?? [])}] DependsOn [{string.Join(",", e.DependsOn ?? [])}]";
}
