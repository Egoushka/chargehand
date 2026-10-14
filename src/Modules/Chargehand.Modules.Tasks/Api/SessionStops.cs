using System.Collections.Concurrent;

namespace Chargehand.Modules.Tasks.Api;

/// <summary>How to stop the session each task of a batch is waiting on now (CHARGEHAND-214): the task runner registers it while it waits, and the batch's operator controls and the console ask.
/// Nothing here outlives the process: a resumed task registers again when it waits again.</summary>
public sealed class SessionStops
{
    private readonly ConcurrentDictionary<string, Func<Task>> _stops = new(StringComparer.Ordinal);

    public void Started(string taskId, Func<Task> stop) => _stops[taskId] = stop;

    public void Ended(string taskId) => _stops.TryRemove(taskId, out _);

    /// <summary>Kills the task's running session container. The session writes no outcome, so the task reads it as a session that failed and ends as it does then.</summary>
    /// <returns>False when the task has no session running now.</returns>
    public async Task<bool> StopAsync(string taskId)
    {
        if (!_stops.TryGetValue(taskId, out var stop))
            return false;
        await stop();
        return true;
    }
}
