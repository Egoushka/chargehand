namespace Chargehand.Modules.Accounts.Api;

/// <summary>Where an account's spend in the UTC month is heading (CHARGEHAND-216, ADR 0076): the month so far plus the trailing 7-day rate until the month ends.</summary>
/// <param name="RatePerDay">Spend of the last 7 days, divided by 7.</param>
/// <param name="ProjectedUsd">Spend by the end of the month at that rate.</param>
/// <param name="CapUsd"><c>max_usd_per_month</c>; null: no cap, so nothing to be over.</param>
/// <param name="OverCap">The projection passes the cap.</param>
/// <param name="CapReachedAt">When the cap is reached at that rate, if within the month; the current time when it is reached already.</param>
public sealed record SpendForecast(decimal RatePerDay, decimal ProjectedUsd, decimal? CapUsd, bool OverCap, DateTimeOffset? CapReachedAt);

public static class SpendForecasts
{
    public const int TrailingDays = 7;

    public static DateTimeOffset TrailingStart(DateTimeOffset now) => now.AddDays(-TrailingDays);

    /// <param name="monthUsd">Spend since <see cref="AccountPolicy.MonthStart"/>.</param>
    /// <param name="trailingUsd">Spend since <see cref="TrailingStart"/>.</param>
    public static SpendForecast Of(decimal monthUsd, decimal trailingUsd, decimal? cap, DateTimeOffset now)
    {
        var rate = trailingUsd / TrailingDays;
        var end = AccountPolicy.NextMonth(now);
        var daysLeft = (decimal)(end - now).TotalDays;
        var projected = decimal.Round(monthUsd + rate * daysLeft, 4);
        DateTimeOffset? reached = cap is not { } limit ? null
            : monthUsd >= limit ? now
            : rate > 0 && (limit - monthUsd) / rate is var days && days < daysLeft ? now.AddDays((double)days)
            : null;
        return new(decimal.Round(rate, 4), projected, cap, cap is { } c && projected > c, reached);
    }
}
