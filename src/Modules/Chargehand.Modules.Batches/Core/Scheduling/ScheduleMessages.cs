using Chargehand.Contracts;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Core.Scheduling;

/// <summary>What happened, as the scheduler's shell saw it. Time arrives in the event (<c>Now</c>); the machine reads no clock. Events name tasks by id, so a journal can hold them and a
/// restart can fold them again.</summary>
internal abstract record ScheduleEvent;

/// <summary>Look at the batch: skip what can never start, then place the next task or say what to wait for.</summary>
internal sealed record Tick(DateTimeOffset Now) : ScheduleEvent;

/// <summary>An operator cancelled the task; if it has not started, it never does.</summary>
internal sealed record OperatorCancelled(string TaskId) : ScheduleEvent;

/// <summary>The placed task got its slot and its host. <paramref name="Runner"/> is the runner the host chose (null with one host); <paramref name="Forced"/> says why it was let in against
/// the rule.</summary>
internal sealed record Admitted(string TaskId, string? Runner, string? Forced, DateTimeOffset Now) : ScheduleEvent;

/// <summary>The host has no room for the placed task yet.</summary>
internal sealed record Held(string TaskId, string Detail, DateTimeOffset Now) : ScheduleEvent;

/// <summary>Nothing will place the task: the batch stops starting tasks.</summary>
internal sealed record Refused(string TaskId, string? Detail, DateTimeOffset Now, ErrorCode? Error = null) : ScheduleEvent;

/// <summary>The placed task did not start this time: another batch holds the shared slots, or a halt cut the host's answer short.</summary>
internal sealed record Blocked(DateTimeOffset Now) : ScheduleEvent;

/// <summary>A running task's live usage, all of it so far.</summary>
internal sealed record Reported(string TaskId, long Tokens, decimal Usd) : ScheduleEvent;

/// <summary>A started task's runner returned. A <see cref="TaskOutcome.Replace"/> outcome means nothing ran and the task asks to be placed again.</summary>
internal sealed record Ended(string TaskId, TaskOutcome Outcome, DateTimeOffset Now) : ScheduleEvent;

/// <summary>The batch was halted.</summary>
internal sealed record Halt : ScheduleEvent;

/// <summary>What the shell does. Effects come first; a decision that needs the shell to wait or act ends with one <see cref="ScheduleStep"/>.</summary>
internal abstract record ScheduleCommand;

/// <summary>Run the task, which carries its upstream branches and its runner. <paramref name="PlacedOn"/> is the runner admission chose, which is told when the task ends.</summary>
internal sealed record Start(string TaskId, ResolvedTask Task, string? PlacedOn) : ScheduleCommand;

/// <summary>Cancel the running task.</summary>
internal sealed record Cancel(string TaskId) : ScheduleCommand;

/// <summary>The task will not start; this is its final outcome.</summary>
internal sealed record Skip(TaskOutcome Outcome) : ScheduleCommand;

/// <summary>A started task's final outcome, after the caps had their say.</summary>
internal sealed record Finish(TaskOutcome Outcome) : ScheduleCommand;

/// <summary>Tell the caller a task waits, or was let in against the rule, or is placed again.</summary>
internal sealed record Hold(string TaskId, string Why) : ScheduleCommand;

/// <summary>A usage or rate limit holds new starts until <paramref name="Until"/>.</summary>
internal sealed record Pause(string TaskId, DateTimeOffset Until) : ScheduleCommand;

internal abstract record ScheduleStep : ScheduleCommand;

/// <summary>Take a shared slot and ask the host for the task (<paramref name="Runner"/>: the runner it must stay on), then report <see cref="Admitted"/>, <see cref="Held"/>,
/// <see cref="Refused"/> or <see cref="Blocked"/>.</summary>
internal sealed record Place(string TaskId, string? Runner) : ScheduleStep;

/// <summary>Wait for a running task to end or report, then report each that ended and <see cref="Tick"/>.</summary>
internal sealed record AwaitTasks : ScheduleStep;

/// <summary>Nothing runs and a limit holds new starts: wait until then, or for a halt, then <see cref="Tick"/>.</summary>
internal sealed record WaitUntil(DateTimeOffset Until) : ScheduleStep;

/// <summary>Nothing runs and only a shared slot or the host is in the way: look again shortly.</summary>
internal sealed record LookAgain : ScheduleStep;

/// <summary>The batch has ended; every task has its outcome, in the order given.</summary>
internal sealed record Done(BatchOutcome Outcome) : ScheduleStep;

internal sealed record ScheduleDecision(ScheduleState State, IReadOnlyList<ScheduleCommand> Commands)
{
    /// <summary>What the shell waits on or does next; null when the event asks for nothing (a report, an end, a cancel, a halt).</summary>
    public ScheduleStep? Step => Commands.Count > 0 ? Commands[^1] as ScheduleStep : null;
}
