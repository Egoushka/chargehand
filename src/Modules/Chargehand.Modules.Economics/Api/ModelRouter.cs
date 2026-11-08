namespace Chargehand.Modules.Economics.Api;

/// <summary>What the router decided for one call (ADR 0095).</summary>
public abstract record RouteDecision(string Tier);

/// <summary>Run on <paramref name="Model"/>, paid by <paramref name="Pool"/>. The model is always named: the runtime never picks one.</summary>
public sealed record RouteChoice(string Tier, string Pool, string Model, decimal? RoomUsd) : RouteDecision(Tier);

/// <summary>No pool may take the call before <paramref name="Until"/>; <paramref name="Standings"/> says why, pool by pool.</summary>
public sealed record RouteWait(string Tier, DateTimeOffset Until, IReadOnlyList<PoolStanding> Standings) : RouteDecision(Tier);

/// <summary>A task's tier as it runs (ADR 0095): <c>escalate_after</c> failed verify steps in a row move it one tier up; it never moves down.</summary>
public sealed record TaskTier(int Tier, int FailuresInARow = 0)
{
    public TaskTier Failed(int escalateAfter, int topTier) =>
        FailuresInARow + 1 >= escalateAfter ? new(Math.Min(Tier + 1, topTier), 0) : this with { FailuresInARow = FailuresInARow + 1 };

    public TaskTier Passed() => this with { FailuresInARow = 0 };
}

/// <summary>Picks a tier by task kind and size and the first pool, in drain order, that has room today and pays for a model of that tier (ADR 0095). Pure apart from the settings.</summary>
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

    /// <summary>The first pool in drain order with room now that pays for a model of <paramref name="tier"/>; a pool without room falls through to the next.</summary>
    /// <param name="exhausted">Pools a provider refused today (a 429, a used quota): no room until the next day.</param>
    /// <param name="estimateUsd">What the call is expected to cost: a paced pool needs at least this much room.</param>
    public RouteDecision Choose(int tier, IReadOnlyList<PoolSpend> spend, DateTimeOffset now, IReadOnlySet<string>? exhausted = null, decimal estimateUsd = 0)
    {
        var models = _tiers[Math.Clamp(tier, 0, TopTier)].Models;
        List<PoolStanding> seen = [];
        foreach (var pool in _settings.Order ?? [])
        {
            if (models.FirstOrDefault(m => pool.Models.Contains(m, StringComparer.Ordinal)) is not { } model)
                continue;
            var standing = PoolBalance.Of(pool, spend, now, Zone, exhausted?.Contains(pool.Name) == true, pool.Kind == PoolKinds.Free ? 0 : estimateUsd);
            if (standing.HasRoom)
                return new RouteChoice(NameOf(tier), pool.Name, model, standing.RoomUsd);
            seen.Add(standing);
        }
        return new RouteWait(NameOf(tier), Until(seen, now), seen);
    }

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
