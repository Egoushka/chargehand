using Chargehand.Kernel;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Pool.Api;

namespace Chargehand.Server;

public sealed record OverviewPoolCeiling(long WorkerTokens, int MaxOpenDecisions);

/// <param name="Reason">The check (or <c>starter</c>, <c>lost</c>, <c>ceiling</c>) that last held or released the item; its detail text is not stored.</param>
public sealed record OverviewPoolQueued(string Key, string Repository, string Area, string Title, int Priority, int? PullRequest, DateTimeOffset EnqueuedAt, string? Reason);

/// <param name="Items">The items the worker holds now.</param>
public sealed record OverviewPoolWorker(string Id, string Repository, string Area, long Tokens, DateTimeOffset StartedAt, int Items);

public sealed record OverviewPoolDone(string Key, string Repository, string Area, string Title, DateTimeOffset DoneAt);

/// <summary>The task pool as the first screen shows it (ADR 0099): what waits and why, the live workers and what finished since a time. Never an item's body, and no link from a worker to a thread.</summary>
public sealed record OverviewPool(OverviewPoolCeiling Ceiling, IReadOnlyList<OverviewPoolQueued> Queued, IReadOnlyList<OverviewPoolWorker> Workers, IReadOnlyList<OverviewPoolDone> Done);

internal static class ConsoleOverviewPool
{
    public const int Cap = 50;

    public static async Task<OverviewPool?> ReadAsync(ConsoleContext c, Principal who, DateTimeOffset since, CancellationToken ct)
    {
        if (!who.IsOwner || c.Settings.Pool is not { } settings || c.Pool is not { } pool)
            return null;
        var queued = (await pool.QueuedAsync(Cap, ct)).Select(i => new OverviewPoolQueued(i.Key, i.Repository, i.Area, Redaction.Scrub(i.Title), i.Priority, i.PullRequest, i.EnqueuedAt, i.Reason)).ToList();
        var live = (await pool.LiveWorkersAsync(Cap, ct)).Select(w => new OverviewPoolWorker(w.Id, w.Scope.Repository, w.Scope.Area, w.Tokens, w.StartedAt, w.Items)).ToList();
        var done = (await pool.DoneSinceAsync(since, Cap, ct)).Select(i => new OverviewPoolDone(i.Key, i.Repository, i.Area, Redaction.Scrub(i.Title), i.DoneAt)).ToList();
        return new OverviewPool(new OverviewPoolCeiling(settings.WorkerTokens, settings.MaxOpenDecisions), queued, live, done);
    }
}
