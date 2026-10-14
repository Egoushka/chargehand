using System.Collections.Immutable;
using Chargehand.Contracts;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Core.Scheduling;

/// <summary>What the scheduler is given and never changes: the batch's tasks in order and its limits (ADR 0039).</summary>
/// <param name="Backoff">How long a rate limit that names no reset holds new starts.</param>
/// <param name="AdmissionInterval">How often a task the host holds asks again; null: no admission, tasks start by count and caps only.</param>
internal sealed record SchedulePlan(ImmutableArray<ResolvedTask> Tasks, int MaxParallel, long? MaxTokensTotal, decimal? MaxUsdTotal, TaskLimits PerTask, TimeSpan Backoff,
    TimeSpan? AdmissionInterval = null)
{
    /// <summary>A task's position by id; a repeated id keeps its last position, as the scheduler always did.</summary>
    public IReadOnlyDictionary<string, int> IndexOf { get; } = Index(Tasks);

    /// <summary>Per task, the positions of the tasks of this batch it depends on, in the order it lists them; ids outside the batch are left out.</summary>
    public ImmutableArray<ImmutableArray<int>> Dependencies { get; } = DependenciesOf(Tasks);

    private static Dictionary<string, int> Index(ImmutableArray<ResolvedTask> tasks)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < tasks.Length; i++)
            index[tasks[i].Id] = i;
        return index;
    }

    private static ImmutableArray<ImmutableArray<int>> DependenciesOf(ImmutableArray<ResolvedTask> tasks)
    {
        var index = Index(tasks);
        return [.. tasks.Select(t => (t.DependsOn ?? []).Where(index.ContainsKey).Select(d => index[d]).ToImmutableArray())];
    }
}

internal enum SlotPhase { Pending, Running, Ended }

/// <summary>One task as the scheduler holds it. Pending: not started, or back after a <see cref="TaskOutcome.Replace"/>. Running: placed and started, with what it has reported. Ended: its final outcome,
/// after the caps had their say; a skipped task ends without starting.</summary>
/// <param name="Runner">The runner admission placed it on; null with one host.</param>
/// <param name="OverTaskCap">It reported more than its own cap and was cancelled for it.</param>
/// <param name="Placements">How often it came back to be placed again.</param>
internal sealed record TaskSlot(SlotPhase Phase = SlotPhase.Pending, TaskOutcome? Outcome = null, string? Runner = null, long Tokens = 0, decimal Usd = 0, bool OverTaskCap = false,
    int Placements = 0);

/// <summary>The scheduler's whole state; <see cref="BatchMachine.Decide"/> is the only thing that makes a new one. A batch is draining once <see cref="StopReason"/> is set or it is
/// <see cref="Halted"/>: nothing more starts and what runs finishes.</summary>
/// <param name="CancelRequested">Tasks an operator cancelled; one that is pending at the next sweep never starts.</param>
/// <param name="DoneTokens">Spent by the tasks that ended, which replaces their reservation.</param>
/// <param name="StopReason">Why the batch starts no more tasks; the tasks left are not started with it.</param>
/// <param name="StopError">The error those tasks carry.</param>
/// <param name="Action">What the caller should do about the stop (a rate-limited subscription); null otherwise.</param>
/// <param name="Overspent">The batch's cap was passed while tasks ran; they were cancelled.</param>
/// <param name="PausedUntil">A usage or rate limit holds new starts until then.</param>
/// <param name="Strikes">Rate limits in a row with no task completing between them.</param>
/// <param name="LimitStop">The batch stopped on the provider's limit: it is retryable after the reset.</param>
/// <param name="AdmitAfter">The host is not asked again before then.</param>
/// <param name="Holding">The task the host holds now; it is reported once while it waits.</param>
/// <param name="Halted">The batch was halted: running tasks are cancelled by the halt itself, and nothing starts.</param>
internal sealed record ScheduleState(SchedulePlan Plan, ImmutableArray<TaskSlot> Slots, ImmutableHashSet<string> CancelRequested, long DoneTokens = 0, decimal DoneUsd = 0,
    string? StopReason = null, ErrorCode? StopError = null, string? Action = null, bool Overspent = false, DateTimeOffset PausedUntil = default, int Strikes = 0, bool LimitStop = false,
    DateTimeOffset AdmitAfter = default, string? Holding = null, bool Halted = false)
{
    public int RunningCount => Slots.Count(s => s.Phase == SlotPhase.Running);

    /// <summary>Positions of the running tasks, in order.</summary>
    public IEnumerable<int> Running => Enumerable.Range(0, Slots.Length).Where(i => Slots[i].Phase == SlotPhase.Running);

    public bool IsPending(int i) => Slots[i].Phase == SlotPhase.Pending;

    public ScheduleState WithSlot(int i, TaskSlot slot) => this with { Slots = Slots.SetItem(i, slot) };
}
