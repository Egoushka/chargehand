using System.Collections.Concurrent;
using Chargehand.Contracts;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Modules.Tasks.Api;

/// <summary>Blind tests (issue 789): a sibling task writes tests from the issue alone and hands over a bundle; the implementer's handover applies it and must pass it. A batch runs both on one runner,
/// so the sibling's bundle reaches the implementer through a slot per implementer.</summary>
public sealed partial class TaskRunner
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TestsBundle?>> _blind = new(StringComparer.Ordinal);

    private TaskCompletionSource<TestsBundle?> BlindSlot(string implementerId) => _blind.GetOrAdd(implementerId, _ => new TaskCompletionSource<TestsBundle?>(TaskCreationOptions.RunContinuationsAsynchronously));

    private string BlindScratch(string implementerId) => Path.Combine(settings.ScratchRoot, "blind", implementerId);

    /// <summary>The blind task's end: its tests are handed over as a bundle with no pull request. Whatever goes wrong, the implementer is released without blind tests.</summary>
    private async Task<(bool Partial, (string RunId, string OutDirectory) Run, HandoverOutcome? Handed)> HandOverBlindAsync(ResolvedTask task, TaskRun run, CancellationToken ct)
    {
        var slot = BlindSlot(task.BlindFor!);
        var handoverRun = (run.RunId, run.OutDirectory);
        if (run.Session is not { Status: SessionStatus.Completed, BundleWritten: true, Commits: > 0 })
        {
            slot.TrySetResult(null);
            return (false, handoverRun, null);
        }
        var handed = await HandOverAsync(task, handoverRun, run.Branch, PullRequestTitle.Of(task.Title, task.Goal), $"Run: {RunNames.Label(run.RunId)}\nTask: {task.Id}", partial: false, run.Stack, run.Session, ct,
            bare: true, blindTask: true);
        TestsBundle? published = null;
        var source = Path.Combine(run.OutDirectory, "chargehand.bundle");
        if (handed.Status == HandoverStatus.Pushed && File.Exists(source))
        {
            Directory.CreateDirectory(BlindScratch(task.BlindFor!));
            var copy = Path.Combine(BlindScratch(task.BlindFor!), "chargehand.bundle");
            File.Copy(source, copy, overwrite: true);
            published = new TestsBundle(copy, run.Branch);
        }
        slot.TrySetResult(published);
        return (false, handoverRun, handed);
    }

    /// <summary>The implementer waits, within its own wall clock, for the blind task's bundle; a missing one means it carries on without.</summary>
    private async Task AwaitBlindAsync(ResolvedTask task, TaskRun run, CancellationToken ct)
    {
        if (!task.BlindTests || run.BlindBundles is not null)
            return;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(run.MaxMinutes));
        TestsBundle? bundle = null;
        try
        {
            bundle = await BlindSlot(task.Id).Task.WaitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        run.BlindBundles = bundle is null ? null : [bundle];
    }

    /// <summary>A handover refused for failing blind tests continues the session while continuations and budget remain; otherwise the work is kept as a partial draft. True: a new session started.</summary>
    private async Task<bool> BlindContinueAsync(ResolvedTask task, TaskRun run, HandoverOutcome? handed, CancellationToken ct)
    {
        if (!task.BlindTests || handed is not { Status: HandoverStatus.NotPushed, Error: ErrorCode.VerificationFailed } || !handed.Reason.StartsWith("blind_tests_failed", StringComparison.Ordinal))
            return false;
        if (run.Continuation - run.Reroutes < settings.Preset.MaxContinuations && HasRoom(run.Total))
        {
            run.BlindFeedback = BlindFeedbackOf(handed);
            if (await ContinueAsync(task, run, ct))
                return true;
            run.BlindFeedback = null;
        }
        run.ForcePartial = true;
        return false;
    }

    private static string BlindFeedbackOf(HandoverOutcome handed)
    {
        var names = handed.Verification?.BlindTests?.Names ?? [];
        return $"{handed.Reason}\n{string.Join('\n', names.Select(n => $"- {n}"))}";
    }

    /// <summary>The continuation after a blind failure tells the session what failed, instead of saying it was stopped.</summary>
    private void ApplyBlindGoal(ResolvedTask task, TaskRun run)
    {
        if (run.BlindFeedback is not { } feedback)
            return;
        run.BlindFeedback = null;
        run.Goal = ContinuationGoal(task.Goal, $"Continuation {run.Continuation} of {settings.Preset.MaxContinuations}. Your work was finished, but it fails acceptance tests that another worker wrote from the issue alone. "
            + $"Fix the code so they pass; do not edit or delete those tests. What failed:\n{feedback}", ReadProgress(run.OutDirectory));
    }

    /// <summary>The slot is always released, so an implementer never waits on a sibling that failed; the implementer's copy of the bundle goes with it.</summary>
    private void ReleaseBlind(ResolvedTask task, TaskRun run)
    {
        if (task.BlindFor is not null)
            BlindSlot(task.BlindFor).TrySetResult(null);
        if (task.BlindTests && (run.Ended || !Detaching))
            DeleteQuietly(BlindScratch(task.Id));
    }
}
