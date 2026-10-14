using System.Collections.Concurrent;

namespace Chargehand.Modules.Batches.Api;

/// <summary>What an operator can do to a batch that is running (CHARGEHAND-214): cancel one task, stop one task's session. The scheduler and the task runner register what can be stopped; the server
/// asks. Nothing here outlives the batch, and a restart starts it empty (a resumed batch registers again as its tasks start).</summary>
public sealed class BatchControl
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = [];
    private readonly HashSet<string> _cancelled = [];
    private readonly ConcurrentDictionary<string, Func<Task>> _stoppers = new(StringComparer.Ordinal);

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
        _stoppers.TryRemove(taskId, out _);
    }

    /// <summary>The task runner says how to stop the session it is waiting on; <see cref="Ended"/> and <see cref="SessionEnded"/> take it back.</summary>
    public void SessionStarted(string taskId, Func<Task> stop) => _stoppers[taskId] = stop;

    public void SessionEnded(string taskId) => _stoppers.TryRemove(taskId, out _);

    /// <summary>Kills the task's running session container. The session writes no outcome, so the task reads it as a session that failed and ends as it does then; nothing is snapshotted or handed over.</summary>
    /// <returns>False when the task has no session running now.</returns>
    public async Task<bool> StopSessionAsync(string taskId)
    {
        if (!_stoppers.TryGetValue(taskId, out var stop))
            return false;
        await stop();
        return true;
    }
}
