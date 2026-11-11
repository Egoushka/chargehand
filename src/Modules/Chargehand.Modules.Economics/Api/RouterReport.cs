namespace Chargehand.Modules.Economics.Api;

/// <summary>One pool of the budget router now (ADR 0095), in drain order. Money is dollars at API price.</summary>
/// <param name="SpentPeriodUsd">What its routed runs cost since its period began.</param>
/// <param name="SpentTodayUsd">What its routed runs cost today, in the profile's time zone.</param>
public sealed record RouterPool(string Name, string Kind, IReadOnlyList<string> Models, DateTimeOffset? PeriodStart, DateTimeOffset? Refill, decimal? RemainingUsd,
    decimal? AllowanceUsd, decimal? RoomUsd, decimal SpentPeriodUsd, decimal SpentTodayUsd, bool HasRoom, string? Why, string? Warning);

/// <summary>A route key (<c>driven</c>, <c>review</c>, <c>driven/L</c>) and the tier it resolves to, with that tier's models in order of preference.</summary>
public sealed record RouterRoute(string Key, string Tier, IReadOnlyList<string> Models);

/// <summary><c>router/v1</c>: the pools and routes of the profile's <c>pools</c> block. Off (<paramref name="Enabled"/> false, nothing listed) when the profile has none.</summary>
public sealed record RouterReport(string ContractVersion, DateTimeOffset GeneratedAt, bool Enabled, string? TimeZone, string? DefaultTier, IReadOnlyList<RouterPool> Pools,
    IReadOnlyList<RouterRoute> Routes);

/// <summary>Reads the router's standing for the console: pure, the same block, spend and time give the same report.</summary>
public static class RouterReportBuilder
{
    public const string Contract = "router/v1";

    public static RouterReport Build(PoolsSettings? settings, IReadOnlyList<PoolSpend> spend, DateTimeOffset now)
    {
        if (settings is not { Enabled: true } || settings.Problems().Any())
            return new(Contract, now, false, null, null, [], []);
        var router = new ModelRouter(settings);
        var today = PoolBalance.DayStart(now, router.Zone);
        var pools = (settings.Order ?? []).Zip(router.Standings(spend, now), (pool, standing) => Pool(pool, standing, spend, today, now)).ToList();
        var tiers = (settings.Tiers ?? []).ToDictionary(t => t.Name, t => t.Models, StringComparer.Ordinal);
        var routes = (settings.Routes ?? new Dictionary<string, string>()).OrderBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => new RouterRoute(r.Key, r.Value, tiers.GetValueOrDefault(r.Value) ?? [])).ToList();
        return new(Contract, now, true, settings.TimeZone, settings.DefaultTier, pools, routes);
    }

    private static RouterPool Pool(PoolSettings pool, PoolStanding standing, IReadOnlyList<PoolSpend> spend, DateTimeOffset today, DateTimeOffset now)
    {
        var mine = spend.Where(s => s.Pool == pool.Name && s.At <= now && (standing.PeriodStart is not { } from || s.At >= from)).ToList();
        return new(pool.Name, pool.Kind, pool.Models, standing.PeriodStart, standing.Refill, standing.RemainingUsd, standing.AllowanceUsd, standing.RoomUsd,
            mine.Sum(s => s.Usd), mine.Where(s => s.At >= today).Sum(s => s.Usd), standing.HasRoom, standing.Why, standing.Warning);
    }
}
