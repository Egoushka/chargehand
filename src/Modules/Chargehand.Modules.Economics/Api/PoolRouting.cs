using System.Collections.Concurrent;
using System.Globalization;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Modules.Economics.Api;

/// <summary>The model a call runs on: the router's choice and who pays for it, or, with the router off, the model the caller resolved itself.</summary>
/// <param name="Model">The <c>provider/model</c> id to send; null only with the router off and nothing mapped, where the runtime picks its own default as before.</param>
/// <param name="Route">The pool and tier that pay; null with the router off.</param>
/// <param name="Warning">A limit the pool is over: it only warns, the call runs.</param>
public sealed record Routed(string? Model, RunRoute? Route, string? Warning = null);

/// <summary>Every pool that may take the call's tier is out until <see cref="Until"/> (ADR 0095): its provider refused it, it expired, or it has not started. The call does not run. A retryable rate limit, so a caller that queues starts it again later.</summary>
public sealed class PoolsWaitException(RouteWait wait) : ChargehandException(ErrorCode.RateLimited,
    $"no budget pool can take tier {wait.Tier} until {wait.Until.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}",
    "Wait until then, or add a pool in the profile's pools block.")
{
    public RouteWait Wait => wait;
}

/// <summary>The budget router as the call sites use it (ADR 0095). Off by default: without an enabled <c>pools</c> block every call keeps the model its caller resolved, and nothing is read.
/// On: each call gets the router's model, the spend per pool is read back from the run log's <see cref="RunRoute"/> records, and a pool a provider refused (a 402, a 429) is out until its refill or the next day.
/// Limits in the settings only warn, through <paramref name="warn"/>; the data class of the call decides which models may see it.</summary>
/// <param name="warn">Told, once per routed call, that its pool is over a limit the profile set. Never told for a probe (<see cref="WaitUntilAsync"/>).</param>
public sealed class PoolRouting(PoolsSettings? settings, IRunLog log, TimeProvider? clock = null, IDataClassifier? classifier = null, Action<string>? warn = null)
{
    private readonly TimeProvider _time = clock ?? TimeProvider.System;
    private readonly ModelRouter? _router = settings is { Enabled: true } on ? new ModelRouter(on) : null;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _exhausted = new(StringComparer.Ordinal);

    public bool On => _router is not null;

    /// <summary>The class of the data a run on <paramref name="repositoryPath"/> touches. With the router off nothing is looked up and the answer is <see cref="DataClass.Restricted"/>, which nothing reads.</summary>
    public Task<DataClass> ClassifyAsync(string? repositoryPath, CancellationToken ct) =>
        _router is null ? Task.FromResult(DataClass.Restricted) : (classifier ?? RestrictedClassifier.Instance).ClassifyAsync(repositoryPath, ct);

    /// <summary>How many pools the router drains, so a caller that falls through to the next pool knows when it has run out.</summary>
    public int Pools => settings?.Order?.Count ?? 0;

    /// <summary>The model for one call under route key <paramref name="kind"/> (the preset name, or <c>intake</c>) and <paramref name="size"/> (S, M, L), over data of class <paramref name="dataClass"/>.</summary>
    /// <param name="unrouted">What the call runs on with the router off.</param>
    /// <exception cref="PoolsWaitException">The router is on and every pool that may take the call is out.</exception>
    /// <exception cref="ChargehandException"><c>repository_not_allowed</c>: no pool pays for a model that may see this data class.</exception>
    public async Task<Routed> RouteAsync(string kind, string? size, string? unrouted, DataClass dataClass, CancellationToken ct)
    {
        var routed = await ChooseAsync(kind, size, unrouted, dataClass, ct);
        if (routed.Warning is { } warning)
            warn?.Invoke(warning);
        return routed;
    }

    private async Task<Routed> ChooseAsync(string kind, string? size, string? unrouted, DataClass dataClass, CancellationToken ct)
    {
        if (_router is null)
            return new(unrouted, null);
        var now = _time.GetUtcNow();
        return _router.Choose(_router.TierFor(kind, size), dataClass, await SpendAsync(ct), now, Exhausted(now)) switch
        {
            RouteChoice choice => new(choice.Model, new RunRoute(choice.Pool, choice.Tier, choice.Model, DataClassName(dataClass)), choice.Warning is { } w ? $"pool '{choice.Pool}': {w}" : null),
            RouteWait wait => throw new PoolsWaitException(wait),
            RouteBlocked blocked => throw new ChargehandException(ErrorCode.RepositoryNotAllowed,
                $"no budget pool pays for a model of tier {blocked.Tier} or above that may see {DataClassName(dataClass)} data",
                "List the models whose provider does not train on prompts in a pool's no_training_models, or name the repository in owner_private_repos if it holds no personal or employer data."),
            _ => throw new InvalidOperationException("unknown route decision"),
        };
    }

    /// <summary>When a call of <paramref name="kind"/> could run again: null when one may run now, or the router is off. For a caller that holds a start rather than fails it.</summary>
    public async Task<DateTimeOffset?> WaitUntilAsync(string kind, string? size, DataClass dataClass, CancellationToken ct)
    {
        try
        {
            await ChooseAsync(kind, size, null, dataClass, ct);
            return null;
        }
        catch (PoolsWaitException e)
        {
            return e.Wait.Until;
        }
        catch (ChargehandException e) when (e.Code == ErrorCode.RepositoryNotAllowed)
        {
            // Waiting does not help: the call starts, and fails with the reason.
            return null;
        }
    }

    /// <summary>A run the router paid for ended. A provider's refusal (a 402 over its budget, a 429, a used quota) takes its pool out: until the pool's next refill for a spent budget, else until the next day.
    /// Returns true when the pool was taken out, so the caller may run the call again on the next pool.</summary>
    public bool Ended(RunRoute? route, ResultError? error)
    {
        if (_router is null || route is null || error is null)
            return false;
        var budget = ChargehandException.BudgetRefused(error.Message);
        if (error.Code != ErrorCode.RateLimited && !budget)
            return false;
        Refused(route, budget);
        return true;
    }

    /// <summary>A provider refused a call on <paramref name="route"/>'s pool: the pool is out until its next refill when the budget is spent, else until the next day.</summary>
    public void Refused(RunRoute route, bool budget)
    {
        if (_router is null)
            return;
        var now = _time.GetUtcNow();
        _exhausted[route.Pool] = (budget ? _router.NextRefill(route.Pool, now) : null) ?? PoolBalance.NextDay(now, _router.Zone);
    }

    /// <summary>Whether some pool may take a call now. Spend never removes a pool (limits only warn), so this reads no run log and a caller that cannot await may ask; the router off: false.</summary>
    public bool CanRoute(string kind, string? size, DataClass dataClass)
    {
        if (_router is null)
            return false;
        var now = _time.GetUtcNow();
        return _router.Choose(_router.TierFor(kind, size), dataClass, [], now, Exhausted(now)) is RouteChoice;
    }

    private static string DataClassName(DataClass dataClass) => dataClass switch
    {
        DataClass.Public => "public",
        DataClass.OwnerPrivate => "owner_private",
        _ => "restricted",
    };

    private HashSet<string> Exhausted(DateTimeOffset now) => [.. _exhausted.Where(e => e.Value > now).Select(e => e.Key)];

    private async Task<IReadOnlyList<PoolSpend>> SpendAsync(CancellationToken ct) => SpendOf((await log.ReadRunsAsync(ct)).Runs);

    /// <summary>Each routed run's cost, charged to its pool when it finished; a run with no price counts nothing.</summary>
    public static IReadOnlyList<PoolSpend> SpendOf(IEnumerable<RunRecord> runs) =>
        [.. runs.Where(r => r.Route is not null && r.Result.Usage.Usd is > 0).Select(r => new PoolSpend(r.Route!.Pool, r.Finished, r.Result.Usage.Usd!.Value))];
}
