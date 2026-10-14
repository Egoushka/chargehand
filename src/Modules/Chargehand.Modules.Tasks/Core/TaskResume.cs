using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Tasks.Core;

/// <summary>How a task takes up after a restart (ADR 0043). <c>Done</c>: it ended, return its outcome; <c>Fresh</c>: begin under a new run id; <c>Attach</c>: wait on the session it has;
/// <c>RestartFirstSession</c>: the first session's start was cut short, start it again under the same run id; <c>RestartContinuation</c>: a continuation's start was cut short, start it again
/// under the same run id from the previous session's bundle; <c>RestartContinuationRenamed</c>: a continuation that never ran is started again from that bundle under a new run id.</summary>
internal enum ResumeKind { Done, Fresh, Attach, RestartFirstSession, RestartContinuation, RestartContinuationRenamed }

/// <summary>A bundle looked for on the host or its output volume; <paramref name="Bundle"/> null: it is gone.</summary>
internal sealed record SeedLookup(string? Bundle);

/// <summary>What the shell has found out so far; null: not asked yet. The facts are asked for one at a time, in the order the decision needs them, so no call is made that the answer does not need.</summary>
/// <param name="StartCut">The journal holds the start step of the entry's run without its outcome (ADR 0061).</param>
/// <param name="CutSeed">The previous session's bundle, for a continuation whose start was cut.</param>
/// <param name="Status">The entry's container as <c>docker inspect</c> sees it.</param>
/// <param name="HasOutcome">The run's output volume holds a session outcome.</param>
/// <param name="Removed">The old attempt's container, volumes and scratch directories were removed.</param>
/// <param name="GoneSeed">The previous session's bundle, for a continuation whose session is gone.</param>
internal sealed record ResumeFacts(bool? StartCut = null, SeedLookup? CutSeed = null, ContainerStatus? Status = null, bool? HasOutcome = null, bool Removed = false, SeedLookup? GoneSeed = null);

/// <summary>What the shell does next: find out one fact, clear the old attempt, or take the task up.</summary>
internal abstract record ResumeStep;

internal sealed record AskStartCut(string RunId) : ResumeStep;

internal sealed record AskCutSeed(string RunId) : ResumeStep;

internal sealed record InspectContainer(string Container) : ResumeStep;

internal sealed record AskOutcome(string RunId) : ResumeStep;

internal sealed record RemoveRun(string RunId, string? Container) : ResumeStep;

internal sealed record AskGoneSeed(string RunId) : ResumeStep;

/// <param name="Seed">The bundle a restarted continuation starts from.</param>
/// <param name="Outcome">The ended task's outcome.</param>
internal sealed record TakeUp(ResumeKind Kind, string? Seed = null, TaskOutcome? Outcome = null) : ResumeStep;

/// <summary>The restart decision (C4 brief, section 3; <c>TaskRunner.Prepare.cs</c>): the task's saved entry and what the shell found out give the next step. Pure: no I/O, no clock.</summary>
internal static class TaskResume
{
    private static readonly TakeUp Fresh = new(ResumeKind.Fresh);
    private static readonly TakeUp Attach = new(ResumeKind.Attach);

    public static ResumeStep Decide(TaskEntry? entry, ResumeFacts facts)
    {
        // C4 brief, section 3, item 3: a task handed over before its follow-up ended returns that outcome; the follow-up is not taken up.
        if (entry is { Phase: TaskPhase.HandedOver or TaskPhase.Failed, Outcome: { } done })
            return new TakeUp(ResumeKind.Done, Outcome: done);
        if (entry is not { Phase: TaskPhase.Started or TaskPhase.Finished, RunId: { } runId })
            return Fresh;
        if (entry.Phase == TaskPhase.Started)
        {
            if (facts.StartCut is not { } cut)
                return new AskStartCut(runId);
            if (cut && Interrupted(entry, facts) is { } again)
                return again;
        }
        return Begun(entry, runId, facts);
    }

    /// <summary>A start the restart cut short runs again under its run id: a first session as it did the first time, a continuation from the previous session's bundle. Null: a continuation
    /// whose bundle is gone, handled as any other begun task.</summary>
    private static ResumeStep? Interrupted(TaskEntry entry, ResumeFacts facts)
    {
        if (entry.Continuation == 0)
            return new TakeUp(ResumeKind.RestartFirstSession);
        if (entry.Runs is not { Count: > 1 } runs)
            return null;
        if (facts.CutSeed is not { } seed)
            return new AskCutSeed(runs[^2]);
        return seed.Bundle is null ? null : new TakeUp(ResumeKind.RestartContinuation, seed.Bundle);
    }

    /// <summary>A begun task whose container is there, or whose output volume holds an outcome, is attached to. Otherwise there is nothing to take up: the old attempt is cleared, and a continuation
    /// starts again from the previous session's bundle, any other task under a new run id.</summary>
    private static ResumeStep Begun(TaskEntry entry, string runId, ResumeFacts facts)
    {
        if (facts.Status is not { } status)
            return new InspectContainer(entry.Container ?? $"chargehand-{runId}");
        if (status != ContainerStatus.Missing)
            return Attach;
        if (facts.HasOutcome is not { } kept)
            return new AskOutcome(runId);
        if (kept)
            return Attach;
        if (!facts.Removed)
            return new RemoveRun(runId, entry.Container);
        if (entry is not { Continuation: > 0, Runs: { Count: > 1 } runs })
            return Fresh;
        if (facts.GoneSeed is not { } seed)
            return new AskGoneSeed(runs[^2]);
        return seed.Bundle is null ? Fresh : new TakeUp(ResumeKind.RestartContinuationRenamed, seed.Bundle);
    }
}
