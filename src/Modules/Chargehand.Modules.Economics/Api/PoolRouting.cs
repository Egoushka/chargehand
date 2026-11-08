using System.Collections.Concurrent;
using System.Globalization;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Modules.Economics.Api;

/// <summary>The model a call runs on: the router's choice and who pays for it, or, with the router off, the model the caller resolved itself.</summary>
/// <param name="Model">The <c>provider/model</c> id to send; null only with the router off and nothing mapped, where the runtime picks its own default as before.</param>
/// <param name="Route">The pool and tier that pay; null with the router off.</param>
public sealed record Routed(string? Model, RunRoute? Route);

/// <summary>No pool has room for the call's tier before <see cref="Until"/> (ADR 0095): the call does not run. A retryable rate limit, so a caller that queues starts it again later.</summary>
public sealed class PoolsWaitException(RouteWait wait) : ChargehandException(ErrorCode.RateLimited,
    $"no budget pool has room for tier {wait.Tier} until {wait.Until.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}",
    "Wait until then, or raise a pool's balance in the profile's pools block.")
{
    public RouteWait Wait => wait;
}

/// <summary>The budget router as the call sites use it (ADR 0095). Off by default: without an enabled <c>pools</c> block every call keeps the model its caller resolved, and nothing is read.
/// On: each call gets the router's model, the spend per pool is read back from the run log's <see cref="RunRoute"/> records, and a pool a provider refused is out until the next day.</summary>
public sealed class PoolRouting(PoolsSettings? settings, IRunLog log, TimeProvider? clock = null)
{
    private readonly TimeProvider _time = clock ?? TimeProvider.System;
    private readonly ModelRouter? _router = settings is { Enabled: true } on ? new ModelRouter(on) : null;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _exhausted = new(StringComparer.Ordinal);

    public bool On => _router is not null;

    /// <summary>The model for one call under route key <paramref name="kind"/> (the preset name, or <c>intake</c>) and <paramref name="size"/> (S, M, L).</summary>
    /// <param name="unrouted">What the call runs on with the router off.</param>
    /// <exception cref="PoolsWaitException">The router is on and no pool has room for the tier.</exception>
    public async Task<Routed> RouteAsync(string kind, string? size, string? unrouted, CancellationToken ct)
    {
        if (_router is null)
            return new(unrouted, null);
        var now = _time.GetUtcNow();
        return _router.Choose(_router.TierFor(kind, size), await SpendAsync(ct), now, Exhausted(now)) switch
        {
            RouteChoice choice => new(choice.Model, new RunRoute(choice.Pool, choice.Tier, choice.Model)),
            RouteWait wait => throw new PoolsWaitException(wait),
            _ => throw new InvalidOperationException("unknown route decision"),
        };
    }

    /// <summary>When a call of <paramref name="kind"/> could run again: null when one may run now, or the router is off. For a caller that holds a start rather than fails it.</summary>
    public async Task<DateTimeOffset?> WaitUntilAsync(string kind, string? size, CancellationToken ct)
    {
        try
        {
            await RouteAsync(kind, size, null, ct);
            return null;
        }
        catch (PoolsWaitException e)
        {
            return e.Wait.Until;
        }
    }

    /// <summary>A run the router paid for ended on a provider's refusal (a 429, a used quota): its pool has no room until the next day.</summary>
    public void Ended(RunRoute? route, ResultError? error)
    {
        if (_router is null || route is null || error?.Code != ErrorCode.RateLimited)
            return;
        _exhausted[route.Pool] = PoolBalance.NextDay(_time.GetUtcNow(), _router.Zone);
    }

    private HashSet<string> Exhausted(DateTimeOffset now) => [.. _exhausted.Where(e => e.Value > now).Select(e => e.Key)];

    private async Task<IReadOnlyList<PoolSpend>> SpendAsync(CancellationToken ct) => SpendOf((await log.ReadRunsAsync(ct)).Runs);

    /// <summary>Each routed run's cost, charged to its pool when it finished; a run with no price counts nothing.</summary>
    public static IReadOnlyList<PoolSpend> SpendOf(IEnumerable<RunRecord> runs) =>
        [.. runs.Where(r => r.Route is not null && r.Result.Usage.Usd is > 0).Select(r => new PoolSpend(r.Route!.Pool, r.Finished, r.Result.Usage.Usd!.Value))];
}
