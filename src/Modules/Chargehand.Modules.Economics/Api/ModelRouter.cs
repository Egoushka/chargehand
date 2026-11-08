namespace Chargehand.Modules.Economics.Api;

/// <summary>What the router decided for one call (ADR 0095).</summary>
public abstract record RouteDecision(string Tier);

/// <summary>Run on <paramref name="Model"/>, paid by <paramref name="Pool"/>. The model is always named: the runtime never picks one.</summary>
/// <param name="Warning">A limit the pool is over (ADR 0091 style: told, never refused).</param>
public sealed record RouteChoice(string Tier, string Pool, string Model, decimal? RoomUsd, string? Warning = null) : RouteDecision(Tier);

/// <summary>Every pool that may take the call is out until <paramref name="Until"/> (its provider refused it, it expired, or it has not started); <paramref name="Standings"/> says why, pool by pool.</summary>
public sealed record RouteWait(string Tier, DateTimeOffset Until, IReadOnlyList<PoolStanding> Standings) : RouteDecision(Tier);

/// <summary>No pool pays for a model, in this tier or above it, that may see data of class <paramref name="DataClass"/>: waiting does not help, the call does not run.</summary>
public sealed record RouteBlocked(string Tier, DataClass DataClass) : RouteDecision(Tier);

/// <summary>A task's tier as it runs (ADR 0095): <c>escalate_after</c> failed verify steps in a row move it one tier up; it never moves down.</summary>
public sealed record TaskTier(int Tier, int FailuresInARow = 0)
{
    public TaskTier Failed(int escalateAfter, int topTier) =>
        FailuresInARow + 1 >= escalateAfter ? new(Math.Min(Tier + 1, topTier), 0) : this with { FailuresInARow = FailuresInARow + 1 };

    public TaskTier Passed() => this with { FailuresInARow = 0 };
}

/// <summary>Picks a tier by task kind and size and the first pool, in drain order, that may take the call and pays for a model of that tier that may see the data (ADR 0095). Pure apart from the settings.</summary>
public sealed class ModelRouter
{
    private readonly PoolsSettings _settings;
    private readonly IReadOnlyList<TierSettings> _tiers;

    public ModelRouter(PoolsSettings settings)
    {
        _settings = settings;
        _tiers = settings.Tiers ?? [];
        PoolClock.TryZone(settings.TimeZone, out var zone);
        Zone = zone;
    }

    public TimeZoneInfo Zone { get; }

    public int TopTier => Math.Max(0, _tiers.Count - 1);

    /// <summary>The tier index for a task: <c>routes[kind/size]</c>, else <c>routes[kind]</c>, else <c>default_tier</c>, else the cheapest.</summary>
    public int TierFor(string kind, string? size = null)
    {
        var routes = _settings.Routes ?? new Dictionary<string, string>();
        var name = (size is { Length: > 0 } && routes.TryGetValue($"{kind}/{size}", out var sized) ? sized : null)
            ?? routes.GetValueOrDefault(kind) ?? _settings.DefaultTier;
        return name is null ? 0 : Math.Max(0, IndexOf(name));
    }

    public int IndexOf(string tier)
    {
        for (var i = 0; i < _tiers.Count; i++)
            if (_tiers[i].Name == tier)
                return i;
        return -1;
    }

    public string NameOf(int tier) => _tiers[Math.Clamp(tier, 0, TopTier)].Name;

    /// <summary>The first pool in drain order that may take the call now and pays for a model of <paramref name="tier"/> that may see <paramref name="dataClass"/>. A pool a provider refused, or that
    /// has expired or not started, falls through to the next pool. A tier with no model that may see the data moves the call one tier up, never down. Limits in the settings never remove a pool.</summary>
    /// <param name="exhausted">Pools a provider refused (a 402, a 429, a used quota): out until their mark lapses.</param>
    public RouteDecision Choose(int tier, DataClass dataClass, IReadOnlyList<PoolSpend> spend, DateTimeOffset now, IReadOnlySet<string>? exhausted = null)
    {
        for (var at = Math.Clamp(tier, 0, TopTier); at <= TopTier; at++)
        {
            var models = _tiers[at].Models;
            var candidates = (_settings.Order ?? []).Select(pool => (Pool: pool, Model: models.FirstOrDefault(m => pool.Models.Contains(m, StringComparer.Ordinal) && (dataClass.MayTrain() || pool.KeepsPrivate(m)))))
                .Where(c => c.Model is not null).ToList();
            if (candidates.Count == 0)
                continue;
            List<PoolStanding> seen = [];
            foreach (var (pool, model) in candidates)
            {
                var standing = PoolBalance.Of(pool, spend, now, Zone, exhausted?.Contains(pool.Name) == true);
                if (standing.HasRoom)
                    return new RouteChoice(NameOf(at), pool.Name, model!, standing.RoomUsd, standing.Warning);
                seen.Add(standing);
            }
            return new RouteWait(NameOf(at), Until(seen, now), seen);
        }
        return new RouteBlocked(NameOf(tier), dataClass);
    }

    /// <summary>When the pool next refills; null: never, or it is not one of the pools.</summary>
    public DateTimeOffset? NextRefill(string pool, DateTimeOffset now) =>
        (_settings.Order ?? []).FirstOrDefault(p => p.Name == pool) is { } found && PoolBalance.Period(found, now, Zone).Refill is { } refill && refill > now ? refill : null;

    /// <summary>The earliest moment one of the pools may have room again: the next day, or a refill before it.</summary>
    private DateTimeOffset Until(IReadOnlyList<PoolStanding> seen, DateTimeOffset now)
    {
        var until = PoolBalance.NextDay(now, Zone);
        foreach (var refill in seen.Select(s => s.Refill).OfType<DateTimeOffset>())
            if (refill > now && refill < until)
                until = refill;
        return until;
    }

    /// <summary>Every pool's standing now, in drain order, for a report.</summary>
    public IReadOnlyList<PoolStanding> Standings(IReadOnlyList<PoolSpend> spend, DateTimeOffset now, IReadOnlySet<string>? exhausted = null) =>
        [.. (_settings.Order ?? []).Select(p => PoolBalance.Of(p, spend, now, Zone, exhausted?.Contains(p.Name) == true))];
}
