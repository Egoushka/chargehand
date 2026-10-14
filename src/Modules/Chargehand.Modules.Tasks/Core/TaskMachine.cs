using Chargehand.Contracts;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Tasks.Core;

/// <summary>A transition of a task's entry (ADR 0043): what the journal's <c>&lt;batch&gt;/tasks</c> flow holds, one event per change, folded by <see cref="TaskMachine.Decide"/>.</summary>
internal abstract record TaskEvent;

/// <summary>The scheduler placed the task on a runner, before anything starts there (ADR 0058).</summary>
internal sealed record TaskPlaced(string Runner) : TaskEvent;

/// <summary>The task's first session is about to start: run id, branch and start time are fixed.</summary>
internal sealed record TaskBegan(string RunId, string Branch, DateTimeOffset Started, DateTimeOffset SessionStarted, BestBundle? Best) : TaskEvent;

/// <summary>A session that never started runs again under a new run id; <paramref name="Runs"/> has it in the place of the old one.</summary>
internal sealed record TaskRunRenamed(string RunId, IReadOnlyList<string> Runs) : TaskEvent;

internal sealed record TaskSessionStarting(DateTimeOffset At) : TaskEvent;

internal sealed record TaskContainerStarted(string Container) : TaskEvent;

/// <summary>The session ended and its output was read; the handover may be partial.</summary>
internal sealed record TaskSessionRead : TaskEvent;

/// <summary>A continuation session (ADR 0044) is about to start on the same branch.</summary>
internal sealed record TaskContinuationBegan(string RunId, IReadOnlyList<string> Runs, int Continuation, SessionOutcome Prior, DateTimeOffset SessionStarted, BestBundle Best, string SessionGoal,
    string StartDetail) : TaskEvent;

/// <summary>The continuation could not start: the previous session is the last, and the entry is back on it.</summary>
internal sealed record TaskContinuationFailed(string RunId, IReadOnlyList<string> Runs, int Continuation) : TaskEvent;

internal sealed record TaskFinished(TaskOutcome Outcome) : TaskEvent;

/// <summary>The runner could not be reached before the task started there: it is placed again from scratch, keeping what planning gave it.</summary>
internal sealed record TaskReplaced : TaskEvent;

/// <summary>The entry a batch snapshot written before the task journal held for the task (ADR 0061, amendment of 2026-10-14). One release only: it goes with the importer
/// (<see cref="TaskJournal.ImportAsync"/>).</summary>
internal sealed record TaskImported(TaskEntry Entry) : TaskEvent;

internal abstract record TaskCommand;

/// <summary>Keep the event in the journal's <c>&lt;batch&gt;/tasks</c> flow (<see cref="TaskJournal"/>).</summary>
internal sealed record JournalTaskEvent(TaskEvent Event) : TaskCommand;

/// <summary>The event does not apply in the entry's phase: nothing is kept and the entry stays as it is. A writer that asks for it has a bug; on a replay of the flow it is skipped.</summary>
internal sealed record RefuseTaskEvent(string Why) : TaskCommand;

/// <summary>Tell the batch the task ended. The batch carries it out: its scheduler gets the outcome back from the run and appends the end again after its caps
/// (<see cref="TaskJournal.FinishedAsync"/>).</summary>
internal sealed record ReportTaskEnd(TaskOutcome Outcome) : TaskCommand;

/// <summary>A task's entry as a state machine (C4 brief, section 3): Pending → Started → Finished, Started again for each continuation (or back to Finished when one cannot start), then
/// HandedOver or Failed; a replace goes back to Pending. Pure: no I/O, no clock. Since the journal is the source of the entry, an event its phase does not take is refused (<see cref="Refusal"/>).</summary>
internal static class TaskMachine
{
    public static (TaskEntry State, IReadOnlyList<TaskCommand> Commands) Decide(TaskEntry state, TaskEvent input) =>
        Refusal(state, input) is { } why ? (state, [new RefuseTaskEvent(why)])
        : (Fold(state, input), input is TaskFinished finished ? [new JournalTaskEvent(input), new ReportTaskEnd(finished.Outcome)] : [new JournalTaskEvent(input)]);

    /// <summary>Why the entry's phase does not take the event; null: it does. An ended task (handed over, or failed with its outcome) takes only another end, as the scheduler's cap rewrite is
    /// (C4 brief, section 3, item 1), and an import. A run's events need a begun task (Started or Finished): a continuation begins from a read session (Finished) and fails back from its start
    /// (Started). The placement, the first start and a replace come before or instead of a run, so any phase but an ended one takes them.</summary>
    private static string? Refusal(TaskEntry e, TaskEvent input)
    {
        var ended = e is { Phase: TaskPhase.HandedOver } or { Phase: TaskPhase.Failed, Outcome: not null };
        var takes = input switch
        {
            TaskFinished or TaskImported => true,
            TaskPlaced or TaskBegan or TaskReplaced => !ended,
            TaskContinuationBegan => e.Phase == TaskPhase.Finished,
            TaskContinuationFailed => e.Phase == TaskPhase.Started,
            _ => e.Phase is TaskPhase.Started or TaskPhase.Finished,
        };
        return takes ? null : $"{input.GetType().Name} does not apply to task {e.Id}, which is {(ended ? "ended" : e.Phase.ToString().ToLowerInvariant())}";
    }

    private static TaskEntry Fold(TaskEntry e, TaskEvent input) => input switch
    {
        TaskPlaced p => e with { Runner = p.Runner },
        TaskBegan b => e with
        {
            Phase = TaskPhase.Started,
            RunId = b.RunId,
            Branch = b.Branch,
            Started = b.Started,
            Container = null,
            Continuation = 0,
            Prior = null,
            Runs = [b.RunId],
            SessionStarted = b.SessionStarted,
            Best = b.Best,
            SessionGoal = null,
            StartDetail = null,
        },
        TaskRunRenamed r => e with { RunId = r.RunId, Runs = r.Runs, Container = null },
        TaskSessionStarting s => e with { SessionStarted = s.At },
        TaskContainerStarted c => e with { Container = c.Container },
        TaskSessionRead => e with { Phase = TaskPhase.Finished },
        TaskContinuationBegan c => e with
        {
            Phase = TaskPhase.Started,
            RunId = c.RunId,
            Container = null,
            Continuation = c.Continuation,
            Prior = c.Prior,
            Runs = c.Runs,
            SessionStarted = c.SessionStarted,
            Best = c.Best,
            SessionGoal = c.SessionGoal,
            StartDetail = c.StartDetail,
        },
        TaskContinuationFailed f => e with { Phase = TaskPhase.Finished, RunId = f.RunId, Container = null, Continuation = f.Continuation, Runs = f.Runs },
        // C4 brief, section 3, item 2: Failed stands for failed, cancelled, needs input and not started alike.
        TaskFinished f => e with { Phase = f.Outcome.State == TaskState.Completed ? TaskPhase.HandedOver : TaskPhase.Failed, Outcome = f.Outcome },
        TaskReplaced => Planned(e),
        TaskImported i => i.Entry,
        _ => throw new InvalidOperationException($"no transition for {input.GetType().Name}"),
    };

    /// <summary>The task as the batch resolved and planned it, with nothing of a run: what its events fold over, and what a replace goes back to.</summary>
    public static TaskEntry Planned(TaskEntry e) => new(e.Id, e.Ref, e.Goal, From: e.From, DependsOn: e.DependsOn, Size: e.Size, SplitFrom: e.SplitFrom, SizeFrom: e.SizeFrom, Title: e.Title);
}
