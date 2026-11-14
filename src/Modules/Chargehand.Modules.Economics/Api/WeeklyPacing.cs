using Chargehand.Contracts;

namespace Chargehand.Modules.Economics.Api;

/// <summary>A subscription's weekly cap spread over its seven days (ADR 0095, amended 2026-11-14): each day adds one share to what may have been used by its end, so a day's unused share is carried forward and overuse comes off later days. Pure.</summary>
public static class WeeklyPacing
{
    /// <summary>The 1/7 share of the weekly cap one day may use.</summary>
    public const double DefaultShare = 1.0 / 7;

    /// <summary>The utilization (a fraction of the cap) the window may have reached by the end of today, in <paramref name="zone"/>: <paramref name="share"/> for each day from the window's start up to today, at most the whole cap.
    /// Null when the window cannot be paced: it did not say when it resets, or it has already reset.</summary>
    public static double? Allowance(UsageWindow window, double share, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (window.ResetsAt is not { } resets || resets <= now)
            return null;
        var start = TimeZoneInfo.ConvertTime(resets.AddDays(-7), zone).Date;
        var days = (TimeZoneInfo.ConvertTime(now, zone).Date - start).Days + 1;
        return Math.Min(1, share * Math.Clamp(days, 1, 7));
    }

    /// <summary>Whether the window's use has reached what today allows. The allowance is rounded to 1e-9 so seven sevenths read as the whole cap.</summary>
    public static bool Spent(UsageWindow window, double share, DateTimeOffset now, TimeZoneInfo zone) =>
        Allowance(window, share, now, zone) is { } allowed && window.Utilization >= allowed - 1e-9;
}
