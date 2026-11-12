namespace Chargehand.Modules.Pool.Api;

/// <summary>What one governor tick did: started a worker for a scope, held it with a reason, or found nothing to start.</summary>
/// <param name="Scope">The scope the tick looked at; null when nothing was startable.</param>
/// <param name="Worker">The started worker; null when nothing started.</param>
/// <param name="Reason"><c>empty</c>, a check's name, or <c>starter</c>; null when a worker started.</param>
/// <param name="Detail">The check's or the starter's text.</param>
public sealed record PoolTick(PoolScope? Scope, string? Worker, string? Reason, string? Detail = null);

/// <summary>The items a worker receives next, or why it receives none (<c>ceiling</c>, <c>released</c>).</summary>
public sealed record PoolNext(IReadOnlyList<PoolItem> Items, string? Reason);

/// <summary>The written item, or the refusal with its stable code.</summary>
public sealed record EnqueueResult(Enqueued? Item, PoolRefusal? Refusal);

/// <summary>The pool's one entry point: producers enqueue, the governor ticks, workers ask for more and release (ADR 0099).</summary>
public sealed class TaskPool(SqlitePoolStore store, TaskPoolSettings settings, IReadOnlyList<IPoolCheck> checks, IPoolStarter starter, TimeProvider time)
{
    /// <summary>Validates and writes one item, unless <see cref="TaskPoolSettings.MaxQueued"/> items already wait; never starts work.</summary>
    public async Task<EnqueueResult> EnqueueAsync(EnqueueRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Validate() is { } refusal)
            return new EnqueueResult(null, refusal);
        if (await store.EnqueueAsync(request, settings.MaxQueued, time.GetUtcNow(), ct).ConfigureAwait(false) is not { } item)
            return new EnqueueResult(null, new PoolRefusal(429, "full", $"the queue holds {settings.MaxQueued} items; enqueue again once workers have taken some"));
        return new EnqueueResult(item, null);
    }

    /// <summary>One governor step: scopes are tried in order until one starts a worker. A scope a check or the starter holds carries the reason and the next one is
    /// tried, so one scope that cannot start never stalls the rest. When none starts, the first scope's hold is returned.</summary>
    public async Task<PoolTick> TickAsync(CancellationToken ct)
    {
        var scopes = await store.StartableAsync(ct).ConfigureAwait(false);
        if (scopes.Count == 0)
            return new PoolTick(null, null, "empty");
        PoolTick? first = null;
        foreach (var scope in scopes)
        {
            var tick = await TryAsync(scope, ct).ConfigureAwait(false);
            if (tick.Worker is not null)
                return tick;
            first ??= tick;
        }
        return first!;
    }

    private async Task<PoolTick> TryAsync(PoolScope scope, CancellationToken ct)
    {
        foreach (var check in checks)
        {
            string? detail;
            try
            {
                detail = await check.HoldAsync(scope, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException && (e is not OperationCanceledException || !ct.IsCancellationRequested))
            {
                // A check that cannot answer holds: starting without it would skip the capacity it guards.
                detail = $"the check failed: {e.GetType().Name}";
            }
            if (detail is null)
                continue;
            await store.HoldAsync(scope, check.Name, ct).ConfigureAwait(false);
            return new PoolTick(scope, null, check.Name, detail);
        }
        PoolTick tick;
        try
        {
            tick = await StartAsync(scope, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException && (e is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            tick = new PoolTick(scope, null, "starter", $"the starter failed: {e.GetType().Name}");
        }
        if (tick.Worker is null)
            await store.HoldAsync(scope, "starter", ct).ConfigureAwait(false);
        return tick;
    }

    private async Task<PoolTick> StartAsync(PoolScope scope, CancellationToken ct)
    {
        var worker = "w-" + Guid.NewGuid().ToString("N");
        var claim = await store.StartWorkerAsync(scope, worker, settings.MaxBatchBytes, time.GetUtcNow(), ct).ConfigureAwait(false);
        string? refusal;
        try
        {
            refusal = await starter.StartAsync(new PoolStartRequest(scope, worker, claim.Items, claim.Card), ct).ConfigureAwait(false);
        }
        catch
        {
            await store.ReleaseAsync(worker, null, "starter", time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        if (refusal is null)
            return new PoolTick(scope, worker, null);
        await store.ReleaseAsync(worker, null, "starter", time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        return new PoolTick(scope, null, "starter", refusal);
    }

    /// <summary>Claims the next batch of queued items of the worker's scope (up to <see cref="TaskPoolSettings.MaxBatchBytes"/>), unless the worker is past its token ceiling or released.</summary>
    public async Task<PoolNext> NextAsync(string worker, CancellationToken ct)
    {
        var live = await store.WorkerAsync(worker, ct).ConfigureAwait(false);
        if (live is null || live.ReleasedAt is not null)
            return new PoolNext([], "released");
        if (live.Tokens > settings.WorkerTokens)
            return new PoolNext([], "ceiling");
        return new PoolNext(await store.ClaimAsync(worker, settings.MaxBatchBytes, time.GetUtcNow(), ct).ConfigureAwait(false), null);
    }

    /// <summary>Records the worker's total token use so far.</summary>
    public Task ReportAsync(string worker, long tokens, CancellationToken ct) => store.ReportAsync(worker, tokens, ct);

    /// <summary>Ends the worker: items it still holds go back to the queue; a card marks a ceiling end and goes to the successor.</summary>
    public Task<bool> ReleaseAsync(string worker, ResumeCard? card, CancellationToken ct) =>
        store.ReleaseAsync(worker, card, card is null ? "done" : "ceiling", time.GetUtcNow(), ct);

    /// <summary>Marks items done, with a reason when they ended without completing.</summary>
    public Task DoneAsync(IReadOnlyList<string> keys, string? reason, CancellationToken ct) => store.DoneAsync(keys, reason, time.GetUtcNow(), ct);

    /// <summary>On start: claims whose worker is not in <paramref name="live"/> go back to the queue with reason <c>lost</c>.</summary>
    public Task<int> ReleaseLostAsync(IReadOnlySet<string> live, CancellationToken ct) => store.ReleaseLostAsync(live, time.GetUtcNow(), ct);

    public Task<IReadOnlyList<PoolItem>> ItemsAsync(PoolScope? scope, CancellationToken ct) => store.ItemsAsync(scope, ct);

    public Task<IReadOnlyList<PoolWorker>> WorkersAsync(CancellationToken ct) => store.WorkersAsync(ct);

    /// <summary>The first queued items, without bodies.</summary>
    public Task<IReadOnlyList<PoolQueuedRow>> QueuedAsync(int limit, CancellationToken ct) => store.QueuedAsync(limit, ct);

    /// <summary>The newest items finished since a time, oldest first, without bodies.</summary>
    public Task<IReadOnlyList<PoolDoneRow>> DoneSinceAsync(DateTimeOffset since, int limit, CancellationToken ct) => store.DoneSinceAsync(since, limit, ct);

    /// <summary>The unreleased workers with the count of items each holds.</summary>
    public Task<IReadOnlyList<PoolLiveWorker>> LiveWorkersAsync(int limit, CancellationToken ct) => store.LiveWorkersAsync(limit, ct);

    public Task<PoolWorker?> WorkerAsync(string worker, CancellationToken ct) => store.WorkerAsync(worker, ct);

    public Task<ResumeCard?> CardAsync(PoolScope scope, CancellationToken ct) => store.CardAsync(scope, ct);
}
