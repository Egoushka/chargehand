using System.Globalization;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Automation;

/// <summary>Where the subscription's current window stands: when it resets, and whether it has quota left (it does not while a running task waits out its rate limit).</summary>
public sealed record QuotaWindow(DateTimeOffset ResetsAt, bool QuotaLeft);

/// <summary>The quota harvester (issue #792): an issue with the harvest label waits until the subscription window is about to reset with quota left, so it runs on quota that would expire unused,
/// and no more of them start in a UTC day than <c>max_per_day</c>. The account's own caps still admit every start.</summary>
internal sealed class QuotaHarvest(AutomationHarvest settings, IntakeState state, Func<DateTimeOffset, QuotaWindow?> window)
{
    /// <summary>Whether an issue with <paramref name="labels"/> is harvest work.</summary>
    public bool Marks(IReadOnlyList<string> labels) => labels.Contains(settings.Label, StringComparer.OrdinalIgnoreCase);

    /// <summary>Why harvest work may not start at <paramref name="now"/>, or null when it may.</summary>
    public string? Hold(DateTimeOffset now)
    {
        if (window(now) is not { } current)
            return "no subscription window is known yet";
        if (!current.QuotaLeft)
            return $"the subscription's quota is used up until {Time(current.ResetsAt)}";
        var opens = current.ResetsAt - TimeSpan.FromMinutes(settings.LeadMinutes);
        if (now < opens)
            return $"waits for the end of the subscription window: from {Time(opens)}";
        var today = state.Tasks.Count(t => t.Harvested && t.Started.UtcDateTime.Date == now.UtcDateTime.Date);
        return today >= settings.MaxPerDay
            ? $"{today.ToString(CultureInfo.InvariantCulture)} harvested starts today (limit {settings.MaxPerDay.ToString(CultureInfo.InvariantCulture)})"
            : null;
    }

    /// <summary>The window as running tasks report it: while one waits out a rate limit, the quota is used up until its reset; after that, the next reset is projected <c>window_hours</c> on from
    /// the last one reported. Kept in memory: after a restart no window is known until a task reports a limit again.</summary>
    public static Func<DateTimeOffset, QuotaWindow?> Reported(TimeSpan length, Func<DateTimeOffset, DateTimeOffset?> pausedUntil)
    {
        DateTimeOffset? last = null;
        return now =>
        {
            if (pausedUntil(now) is { } until)
            {
                last = until;
                return new QuotaWindow(until, false);
            }
            if (last is not { } reset)
                return null;
            var passed = now < reset ? 0 : (now - reset).Ticks / length.Ticks + 1;
            return new QuotaWindow(reset + length * passed, true);
        };
    }

    private static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
