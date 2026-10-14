using System.Collections.Concurrent;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Api;

/// <summary>Runs a task on the runner the scheduler placed it on (ADR 0058): the task runner of that runner's part of the batch, which is made when the first task gets there. A task that has a
/// session on its runner stays on it. A task that could not start there because the runner did not answer comes back <see cref="TaskOutcome.Replace"/>, with the journal reset and the runner reported
/// down, for the scheduler to place again.</summary>
/// <param name="runnerOn">The task runner of a runner by name; it makes the runner's network and test services the first time, and throws when it cannot.</param>
/// <param name="defaultRunner">The runner of a task with none: the first of the profile, which is the only one of a profile that names none.</param>
/// <param name="report">The profile names its runners: the outcome says which one ran the task.</param>
/// <param name="state">The batch's state of its tasks (P3): a task with a session keeps its runner, and a replaced one starts over.</param>
/// <param name="started">Tasks that have a session container, filled by the task runners' events; shared with them.</param>
/// <param name="unreachable">Told a runner's name and why when a task could not start on it.</param>
public sealed class SiteRouter(Func<string, Task<TaskRunner>> runnerOn, string defaultRunner, bool report, ITaskState? state, ConcurrentDictionary<string, byte> started,
    Action<string, string>? unreachable)
{
    public async Task<TaskOutcome> RunAsync(ResolvedTask task, IProgress<TaskUsage> progress, CancellationToken ct)
    {
        var name = string.IsNullOrEmpty(task.Runner) ? defaultRunner : task.Runner;
        var entry = state?.Entry(task.Id);
        if (entry is { Phase: TaskPhase.HandedOver or TaskPhase.Failed, Outcome: { } done })
            return done;
        // A session may be running on the runner already: the task is not placed again, however it goes.
        var begun = entry is { Phase: TaskPhase.Started or TaskPhase.Finished, RunId: not null };
        if (report)
            state?.Update(task.Id, e => e with { Runner = name });
        TaskRunner runner;
        try
        {
            runner = await runnerOn(name);
        }
        catch (ChargehandException e) when (!begun && Unreachable(e) && !ct.IsCancellationRequested)
        {
            return Replace(task, name, e);
        }
        try
        {
            var outcome = await runner.RunAsync(task, progress, ct);
            return report ? outcome with { Runner = name } : outcome;
        }
        catch (ChargehandException e) when (!begun && !started.ContainsKey(task.Id) && Unreachable(e) && !ct.IsCancellationRequested)
        {
            return Replace(task, name, e);
        }
    }

    private static bool Unreachable(ChargehandException e) => e is RunnerUnreachableException;

    private TaskOutcome Replace(ResolvedTask task, string runner, ChargehandException e)
    {
        unreachable?.Invoke(runner, e.Message);
        started.TryRemove(task.Id, out _);
        state?.Update(task.Id, t => new TaskEntry(t.Id, t.Ref, t.Goal, From: t.From, DependsOn: t.DependsOn, Size: t.Size, SplitFrom: t.SplitFrom, SizeFrom: t.SizeFrom, Title: t.Title));
        return new TaskOutcome(task.Id, TaskState.NotStarted, null, $"runner '{runner}' could not be reached ({e.Message}); placing the task again", ErrorCode.ContainerUnavailable, Replace: true);
    }
}
