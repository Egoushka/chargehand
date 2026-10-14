using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Api;

/// <summary>What an operator can do to a batch that is running (CHARGEHAND-214): cancel one task, stop one task's session. The scheduler registers the tasks it runs and the task runners the sessions
/// (<see cref="Sessions"/>); the server asks. Nothing here outlives the batch, and a restart starts it empty (a resumed batch registers again as its tasks start).</summary>
public sealed class BatchControl
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = [];
    private readonly HashSet<string> _cancelled = [];

    /// <summary>The batch's running sessions, which its task runners register (Tasks).</summary>
    public SessionStops Sessions { get; } = new();

    /// <summary>Cancels a task: a running one is cancelled the way a batch cancel cancels it (its session is interrupted and killed, nothing is handed over); one that has not started never starts.</summary>
    /// <returns>False when the task has already ended or the batch does not have it.</returns>
    public bool CancelTask(string taskId, Func<string, bool> isLive)
    {
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
            return true;
        }
    }

    public bool IsCancelled(string taskId)
    {
        lock (_gate)
            return _cancelled.Contains(taskId);
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
