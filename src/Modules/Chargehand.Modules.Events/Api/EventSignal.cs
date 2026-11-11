namespace Chargehand.Modules.Events.Api;

/// <summary>Wakes the readers of the log after a commit, as switchknife's <c>IEventSignal</c> does: <see cref="WaitAsync"/> completes once a <c>seq</c> after the given one is stored.</summary>
public sealed class EventSignal
{
    private readonly Lock gate = new();
    private long latest;
    private TaskCompletionSource next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public EventSignal(long latest = 0) => this.latest = latest;

    /// <summary>The newest committed <c>seq</c>.</summary>
    public long Latest
    {
        get
        {
            lock (gate)
                return latest;
        }
    }

    public void Notify(long seq)
    {
        TaskCompletionSource woken;
        lock (gate)
        {
            if (seq <= latest)
                return;
            latest = seq;
            woken = next;
            next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        woken.TrySetResult();
    }

    /// <summary>The newest committed <c>seq</c>, once it is past <paramref name="after"/>.</summary>
    public async Task<long> WaitAsync(long after, CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (latest > after)
                    return latest;
                wait = next.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
