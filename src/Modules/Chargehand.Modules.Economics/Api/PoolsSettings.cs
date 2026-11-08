using System.Globalization;

namespace Chargehand.Modules.Economics.Api;

/// <summary>The profile's <c>pools</c> block (ADR 0095): the money the model router may spend, in the order it drains it, and which models each kind of task may use. Off unless
/// <paramref name="Enabled"/>: a profile without it runs as before.</summary>
/// <param name="TimeZone">The IANA zone that days, refills and expiries are read in.</param>
/// <param name="EscalateAfter">Failed verify steps in a row on one tier before the task moves one tier up.</param>
/// <param name="DefaultTier">The tier of a task kind <paramref name="Routes"/> does not name; none: the cheapest tier.</param>
/// <param name="Tiers">Cheapest first. Each lists its models in order of preference; a tier is what a model can do, not who pays for it.</param>
/// <param name="Routes">Route key to a tier name: the preset name a call runs under (<c>driven</c>, <c>review</c>, <c>code</c>), or <c>intake</c> for intake and the support check. <c>key/size</c> (<c>driven/L</c>) is more specific and wins.</param>
/// <param name="Order">The pools, in the order they are drained.</param>
public sealed record PoolsSettings(bool Enabled = false, string TimeZone = "UTC", int EscalateAfter = 2, string? DefaultTier = null, IReadOnlyList<TierSettings>? Tiers = null,
    IReadOnlyDictionary<string, string>? Routes = null, IReadOnlyList<PoolSettings>? Order = null)
{
    /// <summary>What is wrong with the block, one line each; none for a block that is off and empty.</summary>
    public IEnumerable<string> Problems() =>
        !Enabled && Tiers is null && Order is null && Routes is null ? [] : Shape().Concat(References());

    private IEnumerable<string> Shape()
    {
        if (!PoolClock.TryZone(TimeZone, out _))
            yield return $"time_zone '{TimeZone}' is not a time zone this machine knows";
        if (EscalateAfter < 1)
            yield return "escalate_after must be at least 1";
        if (Enabled && ((Tiers ?? []).Count == 0 || (Order ?? []).Count == 0))
            yield return "an enabled block needs at least one tier and one pool";
        foreach (var problem in Duplicates((Tiers ?? []).Select(t => t.Name), "tier").Concat(Duplicates((Order ?? []).Select(p => p.Name), "pool")))
            yield return problem;
        foreach (var pool in Order ?? [])
            foreach (var problem in pool.Problems())
                yield return $"pool '{pool.Name}': {problem}";
    }

    private IEnumerable<string> References()
    {
        var tiers = Tiers ?? [];
        var names = tiers.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        if (DefaultTier is { } fallback && !names.Contains(fallback))
            yield return $"default_tier '{fallback}' is not a tier";
        foreach (var (kind, tier) in Routes ?? new Dictionary<string, string>())
            if (!names.Contains(tier))
                yield return $"routes.{kind}: '{tier}' is not a tier";
        var paid = (Order ?? []).SelectMany(p => p.Models).ToHashSet(StringComparer.Ordinal);
        foreach (var tier in tiers.Where(t => !t.Models.Any(paid.Contains)))
            yield return $"tier '{tier.Name}': no pool pays for any of its models";
    }

    private static IEnumerable<string> Duplicates(IEnumerable<string> names, string what) =>
        names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"{what} '{g.Key}' is named more than once");
}

/// <param name="Models">Gateway ids (<c>provider/model</c>), the preferred first.</param>
public sealed record TierSettings(string Name, IReadOnlyList<string> Models);

/// <summary>One source of money or quota (ADR 0095).</summary>
/// <param name="Kind"><see cref="PoolKinds.Free"/>, <see cref="PoolKinds.Fixed"/>, <see cref="PoolKinds.Monthly"/> or <see cref="PoolKinds.Weekly"/>.</param>
/// <param name="Models">The gateway ids this pool pays for.</param>
/// <param name="TotalUsd">A fixed pool's whole sum, a monthly pool's sum per period.</param>
/// <param name="QuotaUsd">A weekly pool's quota per period, in dollars at API price; none: unmetered.</param>
/// <param name="DailyRequests">A free pool's calls per day; none: unknown, so only a caller's report of exhaustion stops it.</param>
/// <param name="Start">A fixed pool: the day its spend is counted from and its pacing starts.</param>
/// <param name="SpreadDays">A fixed pool: the days from <paramref name="Start"/> its sum is spread over; none: no pacing.</param>
/// <param name="Expires">A fixed pool: the day it is gone; none: never.</param>
/// <param name="RefillDay">A monthly pool: the day of the month it refills (clamped to the month's last day).</param>
/// <param name="RefillWeekday">A weekly pool: the day of the week it refills, in English (<c>sunday</c>).</param>
/// <param name="RefillTime">A monthly or weekly pool: the time of day it refills, <c>HH:mm</c>.</param>
public sealed record PoolSettings(string Name, string Kind, IReadOnlyList<string> Models, decimal? TotalUsd = null, decimal? QuotaUsd = null, int? DailyRequests = null,
    DateOnly? Start = null, int? SpreadDays = null, DateOnly? Expires = null, int RefillDay = 1, string? RefillWeekday = null, string RefillTime = "00:00")
{
    internal IEnumerable<string> Problems()
    {
        IEnumerable<string> own = Kind switch
        {
            PoolKinds.Free => DailyRequests is < 1 ? ["daily_requests must be at least 1"] : [],
            PoolKinds.Fixed => FixedProblems(),
            PoolKinds.Monthly => MonthlyProblems(),
            PoolKinds.Weekly => WeeklyProblems(),
            _ => [$"kind '{Kind}' is not one of {PoolKinds.Free}, {PoolKinds.Fixed}, {PoolKinds.Monthly}, {PoolKinds.Weekly}"],
        };
        IEnumerable<string> common = [.. Models.Count == 0 ? ["lists no models"] : Array.Empty<string>(), .. PoolClock.Time(RefillTime) is null ? ["refill_time must be HH:mm"] : Array.Empty<string>()];
        return common.Concat(own);
    }

    private IEnumerable<string> FixedProblems()
    {
        if (TotalUsd is not > 0)
            yield return "a fixed pool needs total_usd above 0";
        if (SpreadDays is < 1)
            yield return "spread_days must be at least 1";
        if (SpreadDays is not null && Start is null)
            yield return "spread_days needs start";
    }

    private IEnumerable<string> MonthlyProblems()
    {
        if (TotalUsd is not > 0)
            yield return "a monthly pool needs total_usd above 0";
        if (RefillDay is < 1 or > 31)
            yield return "refill_day must be 1 to 31";
    }

    private IEnumerable<string> WeeklyProblems()
    {
        if (QuotaUsd is <= 0)
            yield return "quota_usd must be above 0";
        if (PoolClock.Weekday(RefillWeekday) is null)
            yield return "a weekly pool needs refill_weekday, a day of the week in English";
    }

    /// <summary>The time of day it refills; a valid profile always has one.</summary>
    internal TimeOnly RefillAt => PoolClock.Time(RefillTime) ?? TimeOnly.MinValue;
}

public static class PoolKinds
{
    public const string Free = "free";
    public const string Fixed = "fixed";
    public const string Monthly = "monthly";
    public const string Weekly = "weekly";
}

/// <summary>Reading the block's times.</summary>
internal static class PoolClock
{
    public static bool TryZone(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }

    public static TimeOnly? Time(string? text) =>
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    public static DayOfWeek? Weekday(string? text) =>
        Enum.TryParse<DayOfWeek>(text, ignoreCase: true, out var day) && Enum.IsDefined(day) && !int.TryParse(text, out _) ? day : null;
}
