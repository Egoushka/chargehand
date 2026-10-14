using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Journal.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Driven;

/// <summary>The task's first phase: what a restart left to take up, the run ids, branch and caps, and the seed of the first session.</summary>
public sealed partial class TaskRunner
{
    private async Task<TaskRun> PrepareAsync(ResolvedTask task, TaskEntry? entry, CancellationToken ct)
    {
        var (resumed, interrupted, restartSeed) = await ResumeAsync(task, entry, ct);
        var run = NewRun(task, resumed, interrupted, restartSeed);
        Carry(run, task, resumed);
        if (restartSeed is not null && interrupted is null)
        {
            // The same session again under a new run id: the name of the one that never started is not reused.
            run.RunId = RunIds.New();
            run.Runs[^1] = run.RunId;
            var (again, allRuns) = (run.RunId, run.Runs.ToList());
            journal?.Update(task.Id, e => e with { RunId = again, Runs = allRuns, Container = null });
        }
        // A task that continues an earlier one starts with that task's bundle as its best, so a session that adds nothing to it neither continues nor loses it (ADR 0045).
        run.Best = (resumed?.Best ?? (resumed is null && task.From?.Kept is { } carried ? new BestBundle(carried.BundleRun, carried.Commits) : null)) is { } kept
            ? new BestRun(kept.RunId, Path.Combine(settings.ScratchRoot, "out", kept.RunId), kept.Commits) : null;
        run.OutDirectory = Path.Combine(settings.ScratchRoot, "out", run.RunId);
        run.Seed = restartSeed;
        run.Stack = StackOf(task);
        run.Budget = await BudgetAsync(task);
        run.StackDirectory = Path.Combine(settings.ScratchRoot, $"{run.RunId}-stack");
        return run;
    }

    /// <summary>The task as a restart left it: begun (<c>Resumed</c>), its session's start cut short (<c>Interrupted</c>), and the bundle a continuation starts again from (<c>RestartSeed</c>).</summary>
    private async Task<(TaskEntry? Resumed, TaskEntry? Interrupted, string? RestartSeed)> ResumeAsync(ResolvedTask task, TaskEntry? entry, CancellationToken ct)
    {
        var resumed = entry is { Phase: TaskPhase.Started or TaskPhase.Finished, RunId: not null } ? entry : null;
        // A session start the restart cut short runs again under the run id it was given: its step adopts or removes what the cut attempt left, and its finished steps are not repeated.
        var interrupted = resumed is { Phase: TaskPhase.Started } && await StartCutAsync(task.Id, resumed.RunId!, ct) ? resumed : null;
        var restartSeed = interrupted is { Continuation: > 0, Runs: { Count: > 1 } runsSoFar } ? await RestoreBundleAsync(runsSoFar[^2], ct) : null;
        if (interrupted is { Continuation: 0 })
            // A first session starts as it did the first time, under the same run id and branch.
            resumed = null;
        else if (restartSeed is null)
            // A continuation whose seed is gone is handled as any other begun task.
            interrupted = null;
        if (interrupted is null && resumed is not null && await GoneWithoutOutputAsync(resumed, ct))
        {
            // Nothing to take up. A continuation that never started is started again from the previous session's bundle (ADR 0044); any other task begins again
            // under a new run id and branch. The old attempt's leftovers are cleared either way.
            await RemoveRunAsync(resumed.RunId!, resumed.Container);
            restartSeed = resumed.Continuation > 0 && resumed.Runs is { Count: > 1 } known ? await RestoreBundleAsync(known[^2], ct) : null;
            if (restartSeed is null)
                resumed = null;
        }
        return (resumed, interrupted, restartSeed);
    }

    private TaskRun NewRun(ResolvedTask task, TaskEntry? resumed, TaskEntry? interrupted, string? restartSeed)
    {
        var runId = resumed?.RunId ?? interrupted?.RunId ?? RunIds.New();
        var preset = settings.Preset;
        return new TaskRun
        {
            Resumed = resumed,
            Attach = resumed is not null && restartSeed is null,
            RunId = runId,
            Branch = BranchOf(task, resumed, runId),
            Started = resumed?.Started ?? interrupted?.Started ?? DateTimeOffset.UtcNow,
            MaxTurns = task.MaxTurns ?? preset.MaxTurns,
            MaxMinutes = task.MaxMinutes ?? preset.MaxMinutes,
            Runs = [.. resumed?.Runs ?? [runId]],
            OutDirectory = "",
        };
    }

    private static string BranchOf(ResolvedTask task, TaskEntry? resumed, string runId) =>
        resumed?.Branch ?? task.From?.Kept?.Branch ?? task.From?.Pushed?.Branch ?? $"chargehand/{runId}";

    /// <summary>Where a begun task's sessions stood: sessions after the first (ADR 0044), the earlier ones' usage added up, and the latest one's prompt and start text.</summary>
    private static void Carry(TaskRun run, ResolvedTask task, TaskEntry? resumed)
    {
        run.Continuation = resumed?.Continuation ?? 0;
        run.Prior = resumed?.Prior;
        run.Goal = resumed?.SessionGoal ?? task.Goal;
        run.StartDetail = resumed?.StartDetail ?? (task.From is null ? "session container started" : "session container started (continues an earlier task's work)");
    }

    private async Task SeedAsync(ResolvedTask task, TaskRun run, CancellationToken ct)
    {
        Directory.CreateDirectory(run.OutDirectory);
        if (run.Resumed is null && task.From is { } from)
        {
            // The earlier task's bundle seeds the first session, and the handoff its last session left goes after the goal.
            var (bundle, handoff) = await KeptWorkAsync(from, run.Branch, run.OutDirectory, Path.Combine(settings.ScratchRoot, $"{run.RunId}-branch"), ct);
            run.Seed = bundle;
            run.Goal = handoff is null ? run.Goal : $"{run.Goal}\n\nThe handoff the earlier session left:\n\n{Truncated(handoff, ProgressInGoal)}";
        }
        if (run.Resumed is null && run.Stack is { } stack)
        {
            // A branch that holds the work of every task this one depends on; a conflict between them fails the task before any container starts.
            run.Seed = await StackAsync(task, run.Branch, run.OutDirectory, run.StackDirectory, ct);
            run.Goal = $"{run.Goal}\n\n{stack.GoalNote}";
            run.StartDetail = stack.StartDetail;
        }
        if (run.Resumed is null)
            journal?.Update(task.Id, e => e with
            {
                Phase = TaskPhase.Started,
                RunId = run.RunId,
                Branch = run.Branch,
                Started = run.Started,
                Container = null,
                Continuation = 0,
                Prior = null,
                Runs = [run.RunId],
                SessionStarted = DateTimeOffset.UtcNow,
                Best = run.Best is null ? null : new BestBundle(run.Best.RunId, run.Best.Commits),
                SessionGoal = null,
                StartDetail = null
            });
    }

    /// <summary>The bundle of an earlier session on the host: the file the task kept, or else read again from that session's output volume (a restart may have cleared the scratch directory).</summary>
    private async Task<string?> RestoreBundleAsync(string run, CancellationToken ct, string? runner = null)
    {
        var directory = Path.Combine(settings.ScratchRoot, "out", run);
        var bundle = Path.Combine(directory, "chargehand.bundle");
        if (File.Exists(bundle))
            return bundle;
        Directory.CreateDirectory(directory);
        try
        {
            await Tolerant(null, () => VolumesOf(runner).FetchOutputAsync(run, directory, ct), ct);
        }
        catch (ChargehandException e) when (e is not RunnerUnreachableException)
        {
            return null;
        }
        return File.Exists(bundle) ? bundle : null;
    }

    /// <summary>What a task that continues an earlier one starts from: the bundle the earlier task kept (null: it made no commits) and the handoff its last session left (null: none).</summary>
    /// <exception cref="ChargehandException">The earlier task kept a bundle and its output volume no longer holds it.</exception>
    private async Task<(string? Bundle, string? Handoff)> KeptWorkAsync(ContinuedFrom from, string branch, string outDirectory, string directory, CancellationToken ct)
    {
        if (from.Pushed is not null)
        {
            // A finished task's branch is read from the remote, not from an output volume: a person or the CI fixer may have pushed to it since, and the push must be a fast-forward.
            var bundlePath = Path.Combine(outDirectory, "branch.bundle");
            var made = await (brancher ?? new GitBranchSeeder()).BuildAsync(new BranchSeedRequest(branch, settings.SourcePath, settings.BaseCommit, settings.RemoteUrl, settings.Push, settings.ScratchRoot, directory,
                bundlePath, settings.AllowLocalRemote), ct);
            DeleteQuietly(directory);
            return made.Made ? (bundlePath, null) : throw new ChargehandException(ErrorCode.SessionFailed, $"could not start from the pushed branch: {made.Failure}", "Send the comments again; the branch may have been deleted or rewritten.");
        }
        var bundle = from.Kept is null ? null
            : await RestoreBundleAsync(from.Kept.BundleRun, ct, from.Runner) ?? throw new ChargehandException(ErrorCode.InvalidRequest,
                $"the work of the task this one continues is gone: its output volume is kept for {ContinuationSource.KeptFor.TotalDays:0} days after it ended",
                "Send the task again with a goal of its own.");
        await RestoreBundleAsync(from.LastRun, ct, from.Runner);
        return (bundle, ReadProgress(Path.Combine(settings.ScratchRoot, "out", from.LastRun)));
    }

    /// <summary>How a task that depends on others is based (ADR 0049). <paramref name="PullRequestBase"/> is the dependency's branch when there is exactly one, so the pull request shows only this task's
    /// change; null: the default branch, because a pull request has one base and several branches were merged.</summary>
    private sealed record StackBase(string? PullRequestBase, string Note, string GoalNote, string StartDetail);

    private StackBase? StackOf(ResolvedTask task)
    {
        if (task.Upstream is not { Count: > 0 } upstream)
            return null;
        var names = string.Join(", ", upstream.Select(u => $"'{u.Id}'"));
        var goalNote = $"The work of the {(upstream.Count == 1 ? "task" : "tasks")} this one depends on ({names}) is already on your branch, after the base commit. Read `git log` first and build on it; do not redo it.";
        if (upstream.Count == 1)
        {
            var only = upstream[0];
            return new StackBase(only.Branch,
                $"Stacked on `{only.Branch}` (task '{only.Id}'{(only.PrUrl is null ? "" : $", {only.PrUrl}")}): this pull request is based on that branch, so it shows only this task's change. Merge that one first.",
                goalNote, $"session container started (stacked on task '{only.Id}')");
        }
        return new StackBase(null,
            $"Depends on tasks {names}: the branch starts from a merge of their branches ({string.Join(", ", upstream.Select(u => $"`{u.Branch}`"))}), so against `{settings.BaseBranch}` this pull request also shows their changes. "
            + "Merge theirs first, or merge this one and close theirs.",
            goalNote, $"session container started (starts from the merged branches of tasks {names})");
    }

    /// <summary>The seed bundle for a task that depends on others: each dependency's bundle is read back from its last session's output volume, then <see cref="IStackSeeder"/> makes one branch of them.</summary>
    /// <exception cref="ChargehandException"><see cref="ErrorCode.MergeConflict"/> when the branches do not merge; <see cref="ErrorCode.SessionFailed"/> when a bundle is gone or git fails.</exception>
    private async Task<string> StackAsync(ResolvedTask task, string branch, string outDirectory, string directory, CancellationToken ct)
    {
        List<StackInput> inputs = [];
        foreach (var up in task.Upstream!)
            inputs.Add(new StackInput(up.Id, up.Branch, await RestoreBundleAsync(up.RunId, ct, up.Runner) ?? throw new ChargehandException(ErrorCode.SessionFailed,
                $"the work of task '{up.Id}', which this task depends on, is gone: its output volume is no longer there", "Send the tasks again, in one batch.")));
        var bundle = Path.Combine(outDirectory, "stack.bundle");
        var made = await (stacker ?? new GitStackSeeder()).BuildAsync(new StackRequest(branch, settings.SourcePath, settings.BaseCommit, settings.ScratchRoot, directory, bundle, inputs), ct);
        if (made.Conflicts.Count > 0)
            throw new ChargehandException(ErrorCode.MergeConflict,
                $"the branches of the tasks it depends on conflict when merged: task '{made.ConflictWith}' conflicts in {string.Join(", ", made.Conflicts.Take(20))}{(made.Conflicts.Count > 20 ? $" and {made.Conflicts.Count - 20} more" : "")}",
                "Make the tasks change different files, or order them with depends_on so one builds on the other, then send this task again.");
        if (!made.Made)
            throw new ChargehandException(ErrorCode.SessionFailed, $"could not start from the tasks it depends on: {made.Failure}", "Send the tasks again.");
        return bundle;
    }

    /// <summary>A task begun before a restart whose container is gone and whose output volume holds no outcome: there is nothing to take up.</summary>
    private async Task<bool> GoneWithoutOutputAsync(TaskEntry entry, CancellationToken ct) =>
        (await Tolerant(entry.Id, () => engine.InspectAsync(entry.Container ?? $"chargehand-{entry.RunId}", ct), ct)).Status == ContainerStatus.Missing
        && !await HasOutcomeAsync(entry.Id, entry.RunId!, ct);

    private async Task<bool> HasOutcomeAsync(string taskId, string runId, CancellationToken ct)
    {
        var directory = Path.Combine(settings.ScratchRoot, "out", runId);
        Directory.CreateDirectory(directory);
        try
        {
            await Tolerant(taskId, () => volumes.FetchOutputAsync(runId, directory, ct), ct);
        }
        catch (ChargehandException e) when (e is not RunnerUnreachableException)
        {
            return false;
        }
        return File.Exists(Path.Combine(directory, "session-outcome.json"));
    }

    /// <summary>The start of the task's session under <paramref name="runId"/> recorded its intent and no outcome: a restart cut it short (ADR 0061).</summary>
    private async Task<bool> StartCutAsync(string taskId, string runId, CancellationToken ct) =>
        steps is not null && (await steps.UnfinishedAsync(settings.BatchId, ct)).Contains(StepKey.Session(settings.BatchId, taskId, runId));

    private async Task RemoveRunAsync(string runId, string? container)
    {
        await engine.RemoveAsync(container ?? $"chargehand-{runId}", CancellationToken.None);
        await engine.RemoveVolumeAsync(RunnerNames.Work(runId), CancellationToken.None);
        await engine.RemoveVolumeAsync(RunnerNames.Out(runId), CancellationToken.None);
        DeleteQuietly(Path.Combine(settings.ScratchRoot, "out", runId));
        DeleteQuietly(Path.Combine(settings.ScratchRoot, runId));
    }
}
