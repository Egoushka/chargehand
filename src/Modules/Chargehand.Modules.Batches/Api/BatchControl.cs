using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Api;

/// <summary>What an operator can do to a batch that is running (CHARGEHAND-214): cancel one task, stop one task's session. The scheduler registers the tasks it runs and the task runners the sessions
/// (<see cref="Sessions"/>); the server asks. A cancel of a task that has not started is kept in the batch's journal once its scheduler runs (ADR 0063), so a restart keeps it; the rest starts
/// empty after a restart (a resumed batch registers again as its tasks start).</summary>
public sealed class BatchControl
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = [];
    private readonly HashSet<string> _cancelled = [];
    private Func<string, Task>? _keep;

    /// <summary>The batch's running sessions, which its task runners register (Tasks).</summary>
    public SessionStops Sessions { get; } = new();

    /// <summary>Cancels a task: a running one is cancelled the way a batch cancel cancels it (its session is interrupted and killed, nothing is handed over); one that has not started never starts.
    /// The cancel of one that has not started is in the batch's journal when this returns, once the scheduler runs; one asked before is journaled as the scheduler starts.</summary>
    /// <returns>False when the task has already ended or the batch does not have it.</returns>
    public async Task<bool> CancelTaskAsync(string taskId, Func<string, bool> isLive)
    {
        Func<string, Task>? keep;
        lock (_gate)
        {
            if (_cancelled.Contains(taskId))
                return true;
            if (_running.TryGetValue(taskId, out var cts))
            {
                _cancelled.Add(taskId);
                cts.Cancel();
                return true;
            }
            if (!isLive(taskId))
                return false;
            _cancelled.Add(taskId);
            keep = _keep;
        }
        if (keep is not null)
            await keep(taskId);
        return true;
    }

    public bool IsCancelled(string taskId)
    {
        lock (_gate)
            return _cancelled.Contains(taskId);
    }

    /// <summary>The scheduler keeps the cancels from now on (null: no more); it is given the ones asked before, to keep itself.</summary>
    internal IReadOnlyList<string> Keep(Func<string, Task>? keep)
    {
        lock (_gate)
        {
            _keep = keep;
            return [.. _cancelled.Where(id => !_running.ContainsKey(id))];
        }
    }

    internal void Started(string taskId, CancellationTokenSource cts)
    {
        lock (_gate)
            _running[taskId] = cts;
    }

    internal void Ended(string taskId)
    {
        lock (_gate)
            _running.Remove(taskId);
        Sessions.Ended(taskId);
    }

    /// <summary>Kills the task's running session container. The session writes no outcome, so the task reads it as a session that failed and ends as it does then; nothing is snapshotted or handed over.</summary>
    /// <returns>False when the task has no session running now.</returns>
    public Task<bool> StopSessionAsync(string taskId) => Sessions.StopAsync(taskId);
}
