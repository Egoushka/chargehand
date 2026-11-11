namespace Chargehand.Modules.Economics.Api;

/// <summary>One call's spend against a pool: what the router reads balances from. A free call adds no dollars and counts as one request.</summary>
public sealed record PoolSpend(string Pool, DateTimeOffset At, decimal Usd);

/// <summary>Where a pool stands now (ADR 0095).</summary>
/// <param name="PeriodStart">When the current period began: the last refill, a fixed pool's start; null: since ever.</param>
/// <param name="Refill">When the pool next refills; null: never.</param>
/// <param name="Horizon">What the remainder is spread up to: the refill, or a fixed pool's spread end or expiry; null: no pacing.</param>
/// <param name="RemainingUsd">The period's balance less all of its spend so far; null: not metered in dollars.</param>
/// <param name="AllowanceUsd">Today's share: the remainder at the start of today over the days left to <paramref name="Horizon"/>; null: not paced.</param>
/// <param name="RoomUsd">What today may still spend: <paramref name="AllowanceUsd"/> less today's spend; null: unbounded.</param>
/// <param name="HasRoom">The pool may take a call now: it has not expired, has started, and its provider has not refused it. A configured amount or pace never takes it away.</param>
/// <param name="Why">Why the pool may not take a call: <c>expired</c>, <c>not started</c> or <c>exhausted</c>.</param>
/// <param name="Warning">A configured limit the pool is over (the balance is spent, today's allowance is used, the day's requests are used). It only warns: the pool still takes calls until its provider refuses.</param>
public sealed record PoolStanding(string Pool, DateTimeOffset? PeriodStart, DateTimeOffset? Refill, DateTimeOffset? Horizon, decimal? RemainingUsd, decimal? AllowanceUsd,
    decimal? RoomUsd, bool HasRoom, string? Why = null, string? Warning = null);

/// <summary>A pool's period and its daily allowance with carry-over (ADR 0095): pure, the same spend and time give the same standing. The balance and the allowance are figures to warn by, never a reason to refuse a call.</summary>
public static class PoolBalance
{
    public static PoolStanding Of(PoolSettings pool, IEnumerable<PoolSpend> spend, DateTimeOffset now, TimeZoneInfo zone, bool exhausted = false)
    {
        var today = DayStart(now, zone);
        var (periodStart, refill, horizon, balance) = Period(pool, now, zone);
        if (pool.Kind == PoolKinds.Fixed && pool.Expires is { } expires && now >= Local(expires.ToDateTime(TimeOnly.MinValue), zone))
            return new(pool.Name, periodStart, null, null, 0, 0, 0, false, "expired");
        if (periodStart > now)
            return new(pool.Name, periodStart, refill, horizon, balance, 0, 0, false, "not started");
        var mine = spend.Where(s => s.Pool == pool.Name && (periodStart is null || s.At >= periodStart) && s.At <= now).ToList();
        if (exhausted)
            return new(pool.Name, periodStart, refill, horizon, balance - mine.Sum(s => s.Usd), null, 0, false, "exhausted");
        if (pool.Kind == PoolKinds.Free)
            return Free(pool, mine.Count(s => s.At >= today), periodStart);
        if (balance is null)
            return new(pool.Name, periodStart, refill, horizon, null, null, null, true);
        var before = mine.Where(s => s.At < today).Sum(s => s.Usd);
        var spentToday = mine.Where(s => s.At >= today).Sum(s => s.Usd);
        var remaining = balance.Value - before - spentToday;
        var allowance = horizon is { } end ? (balance.Value - before) / DaysLeft(today, end) : balance.Value - before;
        var room = Math.Min(allowance - spentToday, remaining);
        return new(pool.Name, periodStart, refill, horizon, remaining, allowance, room, true, null, room > 0 ? null : remaining <= 0 ? "empty" : "today's allowance is spent");
    }

    private static PoolStanding Free(PoolSettings pool, int requestsToday, DateTimeOffset? periodStart)
    {
        var over = pool.DailyRequests is { } quota && requestsToday >= quota;
        return new(pool.Name, periodStart, null, null, null, null, null, true, null, over ? "today's requests are used" : null);
    }

    /// <summary>Whole days from the start of today to <paramref name="horizon"/>, rounded up, at least one: today is a day of the period.</summary>
    internal static decimal DaysLeft(DateTimeOffset today, DateTimeOffset horizon) => Math.Max(1, (decimal)Math.Ceiling((horizon - today).TotalDays));

    /// <summary>The period now falls in, when it ends, what it is paced to, and its balance (null: not metered).</summary>
    internal static (DateTimeOffset? Start, DateTimeOffset? Refill, DateTimeOffset? Horizon, decimal? Balance) Period(PoolSettings pool, DateTimeOffset now, TimeZoneInfo zone)
    {
        switch (pool.Kind)
        {
            case PoolKinds.Fixed:
                var start = pool.Start is { } s ? Local(s.ToDateTime(TimeOnly.MinValue), zone) : (DateTimeOffset?)null;
                var spreadEnd = start is { } from && pool.SpreadDays is { } days ? Local(pool.Start!.Value.AddDays(days).ToDateTime(TimeOnly.MinValue), zone) : (DateTimeOffset?)null;
                var expiry = pool.Expires is { } e ? Local(e.ToDateTime(TimeOnly.MinValue), zone) : (DateTimeOffset?)null;
                DateTimeOffset? horizon = spreadEnd is { } a && expiry is { } b ? (a < b ? a : b) : spreadEnd ?? expiry;
                return (start, null, horizon is { } h && h > now ? h : null, pool.TotalUsd);
            case PoolKinds.Monthly:
                var (last, next) = Monthly(pool, now, zone);
                return (last, next, next, pool.TotalUsd);
            case PoolKinds.Weekly:
                var (lastWeek, nextWeek) = Weekly(pool, now, zone);
                return (lastWeek, nextWeek, nextWeek, pool.QuotaUsd);
            default:
                return (DayStart(now, zone), null, null, null);
        }
    }

    private static (DateTimeOffset Last, DateTimeOffset Next) Monthly(PoolSettings pool, DateTimeOffset now, TimeZoneInfo zone)
    {
        var month = MonthOf(now, zone);
        var last = RefillIn(month, pool, zone);
        if (last > now)
            last = RefillIn(month.AddMonths(-1), pool, zone);
        return (last, RefillIn(MonthOf(last, zone).AddMonths(1), pool, zone));
    }

    private static DateTime MonthOf(DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone).DateTime;
        return new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
    }

    private static DateTimeOffset RefillIn(DateTime month, PoolSettings pool, TimeZoneInfo zone) =>
        Local(month.AddDays(Math.Min(pool.RefillDay, DateTime.DaysInMonth(month.Year, month.Month)) - 1).Add(pool.RefillAt.ToTimeSpan()), zone);

    private static (DateTimeOffset Last, DateTimeOffset Next) Weekly(PoolSettings pool, DateTimeOffset now, TimeZoneInfo zone)
    {
        var day = PoolClock.Weekday(pool.RefillWeekday) ?? DayOfWeek.Sunday;
        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        var candidate = local.Date.AddDays(-(((int)local.DayOfWeek - (int)day + 7) % 7)).Add(pool.RefillAt.ToTimeSpan());
        if (Local(candidate, zone) > now)
            candidate = candidate.AddDays(-7);
        return (Local(candidate, zone), Local(candidate.AddDays(7), zone));
    }

    /// <summary>The start of the day <paramref name="now"/> falls in, in <paramref name="zone"/>.</summary>
    public static DateTimeOffset DayStart(DateTimeOffset now, TimeZoneInfo zone) => Local(TimeZoneInfo.ConvertTime(now, zone).DateTime.Date, zone);

    /// <summary>The start of the next day.</summary>
    public static DateTimeOffset NextDay(DateTimeOffset now, TimeZoneInfo zone) => Local(TimeZoneInfo.ConvertTime(now, zone).DateTime.Date.AddDays(1), zone);

    /// <summary>A wall-clock time in <paramref name="zone"/>; a time a clock change skips is read with the offset after the change.</summary>
    internal static DateTimeOffset Local(DateTime wall, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
            unspecified = unspecified.AddHours(1);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }
}
