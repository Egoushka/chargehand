using System.Globalization;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Automation;

/// <summary>When the sweep's clock fires: <c>at</c> times of the day in the profile's time zone, on the days it runs. A time the zone skips (a spring-forward gap) fires once, just after the gap;
/// a time it repeats (a fall-back hour) fires at its first occurrence.</summary>
public static class SweepSchedule
{
    /// <summary>How late a slot may still be run (a restart, or a sweep that was busy): a starting value.</summary>
    public static readonly TimeSpan MissWindow = TimeSpan.FromMinutes(60);

    /// <returns>The first slot after <paramref name="now"/>; null when no day is allowed or no time is set.</returns>
    public static DateTimeOffset? NextAfter(SweepSettings s, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(s.TimeZone);
        var start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        foreach (var slot in Slots(s, zone, start.AddDays(-1), 9))
            if (slot > now)
                return slot;
        return null;
    }

    /// <returns>Whether a slot fell after <paramref name="lastRun"/>, up to <paramref name="now"/>, and is no more than <paramref name="window"/> old.</returns>
    public static bool MissedWithin(SweepSettings s, DateTimeOffset lastRun, DateTimeOffset now, TimeSpan window)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(s.TimeZone);
        var start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        foreach (var slot in Slots(s, zone, start.AddDays(-2), 3))
            if (slot > lastRun && slot <= now && now - slot <= window)
                return true;
        return false;
    }

    private static IEnumerable<DateTimeOffset> Slots(SweepSettings s, TimeZoneInfo zone, DateOnly first, int days)
    {
        var times = s.Times.Select(t => TimeOnly.ParseExact(t, "HH:mm", CultureInfo.InvariantCulture)).Distinct().Order().ToList();
        var allowed = s.RunDays.ToHashSet();
        for (var i = 0; i < days; i++)
        {
            var day = first.AddDays(i);
            if (!allowed.Contains(day.DayOfWeek))
                continue;
            var slots = times.Select(t => Instant(day.ToDateTime(t), zone)).Distinct().Order();
            foreach (var slot in slots)
                yield return slot;
        }
    }

    private static DateTimeOffset Instant(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
            return new DateTimeOffset(unspecified, zone.BaseUtcOffset);
        var offset = zone.IsAmbiguousTime(unspecified) ? zone.GetAmbiguousTimeOffsets(unspecified).Max() : zone.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset);
    }
}
