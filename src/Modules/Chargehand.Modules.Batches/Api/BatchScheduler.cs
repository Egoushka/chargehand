using Chargehand.Modules.Batches.Core.Scheduling;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Api;

/// <summary>Runs one task to its end and reports its usage as it goes (<see cref="ITaskRunner.RunAsync"/>): the batch's scheduler starts tasks through it.</summary>
public delegate Task<TaskOutcome> RunTask(ResolvedTask task, IProgress<TaskUsage> progress, CancellationToken ct);

/// <summary>Runs a batch's tasks under its limits (ADR 0039): at most <c>MaxParallel</c> at once, tasks in order, and a task starts only if the
/// batch could still afford it. "Afford" reserves each running task's whole cap, so two tasks at 2000 tokens each against a 5000 cap leave no
/// room for a third until they finish and their real spend replaces the reservation. A batch that ends up over its cap by more than one task's
/// cap while tasks are still running has them cancelled; so has a task that reports more than its own cap (live usage reaches the scheduler while the task
/// runs, so the caps bind before it ends). A task that ended on a usage or rate limit holds new starts until the limit lifts (its reset time, else a fixed backoff; a reset
/// further off than <see cref="MaxPause"/> stops the batch instead); running tasks go on. An unavailable provider stops new starts and says how to switch to an
/// API key; nothing switches by itself. With an <paramref name="admission"/>, a task also waits until the host has memory for its request (ADR 0045); a running task is
/// never stopped for it. A task with <c>DependsOn</c> starts only once all of those ended <c>completed</c>, with their branches in <see cref="ResolvedTask.Upstream"/> (ADR 0049); one whose
/// dependency failed, asked a question, was cancelled or was not started is <c>not_started</c> with that reason and holds no slot. A halt (the token) cancels what runs and starts nothing more.</summary>
/// <param name="backoff">How long a limit that names no reset holds new starts; <see cref="UsageLimit.DefaultBackoff"/> when null.</param>
/// <param name="admission">Shared by every batch of the server; null: tasks start by count and caps only. With several runners (ADR 0058) it also names the one a task runs on; a task whose runner
/// could not be reached before its session started (<see cref="TaskOutcome.Replace"/>) is asked for again, up to <see cref="MaxPlacements"/> times in all.</param>
/// <param name="taskRequestMb">What the host must have free for a task: the working set its repository's sessions have measured (<c>MemoryRequest</c>), not its container's limit.</param>
/// <param name="time">The clock rate-limit pauses and admission intervals are read on; <see cref="TimeProvider.System"/> when null.</param>
/// <param name="journal">The batch's task journal: the scheduler keeps its ends and the operator's cancels there and, on a restart, takes up the pause, the placements and the cancels it left
/// (ADR 0063). Null: nothing outlives the process.</param>
/// <remarks>The decisions are <see cref="BatchMachine"/>'s; this class runs them: it starts tasks, waits, and feeds back what happened.</remarks>
public sealed class BatchScheduler(RunTask run, TimeSpan? backoff = null, IAdmission? admission = null, int taskRequestMb = 0, BatchControl? control = null, TimeProvider? time = null,
    TaskJournal? journal = null)
{
    public BatchScheduler(ITaskRunner runner, TimeSpan? backoff = null, IAdmission? admission = null, int taskRequestMb = 0, BatchControl? control = null, TimeProvider? time = null,
        TaskJournal? journal = null)
        : this(runner.RunAsync, backoff, admission, taskRequestMb, control, time, journal)
    {
    }

    /// <summary>The longest wait for a limit to lift; past it the batch stops and the caller retries.</summary>
    public static readonly TimeSpan MaxPause = BatchMachine.MaxPause;

    public const string RateLimitedAction = BatchMachine.RateLimitedAction;

    public const string RepeatedLimitAction = BatchMachine.RepeatedLimitAction;

    /// <summary>How many runners a task is placed on before its runner's silence is the task's failure.</summary>
    public const int MaxPlacements = BatchMachine.MaxPlacements;

    /// <param name="onFinished">Told each started task's final outcome as it ends, after the caps have had their say; the next step waits for it.</param>
    /// <param name="onHold">Told a task's id and why, when it first waits for the host and when it is let in against the rule.</param>
    /// <param name="onSkipped">Told the outcome of a task that will not start because a task it depends on did not complete; the next step waits for it.</param>
    /// <param name="onPause">Told a task's id and when the limit it hit lifts, when a usage or rate limit holds new starts.</param>
    public async Task<BatchOutcome> RunAsync(IReadOnlyList<ResolvedTask> tasks, BatchLimits limits, CancellationToken ct, Func<TaskOutcome, Task>? onFinished = null,
        Action<string, string>? onHold = null, Func<TaskOutcome, Task>? onSkipped = null, Action<string, DateTimeOffset>? onPause = null)
    {
        var plan = new SchedulePlan([.. tasks], limits.MaxParallel, limits.MaxTokensTotal, limits.MaxUsdTotal, limits.PerTask, backoff ?? UsageLimit.DefaultBackoff, admission?.Interval);
        using var shell = new BatchSchedulerRun(plan, new ScheduleHost(run, limits.Global, admission, taskRequestMb, control, time ?? TimeProvider.System, journal, limits.Gate),
            new ScheduleCallbacks(onFinished, onHold, onSkipped, onPause), ct);
        return await shell.RunAsync();
    }
}
