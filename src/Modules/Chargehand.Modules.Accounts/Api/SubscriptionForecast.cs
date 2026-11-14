using System.Globalization;
using Chargehand.Contracts;

namespace Chargehand.Modules.Accounts.Api;

/// <summary>One of a subscription's usage windows, forecast from its readings in the call log (ADR 0098, issue #441).</summary>
/// <param name="Window"><see cref="SubscriptionUsage.FiveHourName"/> or <see cref="SubscriptionUsage.SevenDayName"/>.</param>
/// <param name="Utilization">The latest reading's share of the window, as the provider stated it; never recomputed.</param>
/// <param name="ResetsAt">When the window resets: the latest reading's own reset time, or, once that has passed with no newer reading, the next one its length puts after it.</param>
/// <param name="RatePerHour">How fast <see cref="Utilization"/> rose over the readings since the window's current run started: its newest reading minus its oldest, divided by the
/// hours between them. Zero with one reading, or when the readings do not reach back past a reset this forecast had to project.</param>
/// <param name="ClosesAt">When <see cref="RatePerHour"/> would carry <see cref="Utilization"/> to 1, projected from now; null when the rate does not climb, or when <see cref="ResetsAt"/>
/// arrives first and clears the window before it would.</param>
public sealed record UsageWindowForecast(string Window, double Utilization, DateTimeOffset ResetsAt, double RatePerHour, DateTimeOffset? ClosesAt);

/// <summary>Both of a subscription's windows, forecast from the readings the call log carries (ADR 0098, issue #441); a window the log never reported stays null.</summary>
public sealed record SubscriptionForecast(UsageWindowForecast? FiveHour = null, UsageWindowForecast? SevenDay = null)
{
    /// <summary>The window, if either, forecast to close within <paramref name="horizon"/> of <paramref name="now"/>: the one a large task should wait out rather than race. The
    /// sooner of the two when both do.</summary>
    public UsageWindowForecast? ClosingWithin(DateTimeOffset now, TimeSpan horizon) =>
        new[] { FiveHour, SevenDay }.Where(w => w?.ClosesAt is { } closes && closes - now <= horizon).MinBy(w => w!.ClosesAt);
}

/// <summary>Forecasts a subscription's five-hour and seven-day windows from the call log's readings of them (ADR 0098, issue #441): when each window resets, and -- when
/// recent use is climbing fast enough to use it up first -- when it is forecast to close before that reset. A forecast that a window is about to close is the reason to hold
/// a large task back rather than start it: better to lose the wait than to lose the run to the window mid-task (the problem ADR 0096 resumes from after the fact).</summary>
public static class UsageForecasts
{
    public static readonly TimeSpan FiveHourLength = TimeSpan.FromHours(5);
    public static readonly TimeSpan SevenDayLength = TimeSpan.FromDays(7);

    /// <summary>How soon a forecast close counts as "shortly before a window closes" (issue #441): long enough that a large task started now could plausibly still be
    /// running when it arrives.</summary>
    public static readonly TimeSpan LargeTaskHorizon = TimeSpan.FromMinutes(30);

    /// <summary><paramref name="readings"/> is the call log's history, oldest first (<c>IRunLog.SubscriptionUsageAsync</c>); null when it holds a reading of neither window.</summary>
    public static SubscriptionForecast? Of(IReadOnlyList<(DateTimeOffset At, SubscriptionUsage Usage)> readings, DateTimeOffset now)
    {
        var five = Window(readings, SubscriptionUsage.FiveHourName, u => u.FiveHour, FiveHourLength, now);
        var seven = Window(readings, SubscriptionUsage.SevenDayName, u => u.SevenDay, SevenDayLength, now);
        return five is null && seven is null ? null : new(five, seven);
    }

    /// <summary>Why a large task should wait for a reset rather than start at <paramref name="now"/>, or null when neither window forecasts a close within
    /// <see cref="LargeTaskHorizon"/> (issue #441).</summary>
    public static string? LargeTaskHoldReason(SubscriptionForecast? forecast, DateTimeOffset now) =>
        forecast?.ClosingWithin(now, LargeTaskHorizon) is { } closing
            ? $"the subscription's {closing.Window} window is forecast to close at {Stamp(closing.ClosesAt!.Value)} before it resets at {Stamp(closing.ResetsAt)}; "
                + "wait for the reset before starting a large task"
            : null;

    private static UsageWindowForecast? Window(
        IReadOnlyList<(DateTimeOffset At, SubscriptionUsage Usage)> readings, string name, Func<SubscriptionUsage, UsageWindow?> of, TimeSpan length, DateTimeOffset now)
    {
        List<(DateTimeOffset At, UsageWindow Window)> seen = [];
        foreach (var (at, usage) in readings)
            if (of(usage) is { } w)
                seen.Add((at, w));
        if (seen.Count == 0)
            return null;
        var (latestAt, latest) = seen[^1];
        var resetsAt = Project(latest.ResetsAt ?? latestAt, length, now);
        // A reset already rolled forward past the latest reading's own means none of the readings say anything about the window's current run.
        var rolledOver = latest.ResetsAt is { } known && resetsAt != known;
        var current = rolledOver ? [] : seen.Where(r => r.Window.ResetsAt == latest.ResetsAt).ToList();
        var rate = 0.0;
        if (current.Count > 1)
        {
            var hours = (current[^1].At - current[0].At).TotalHours;
            if (hours > 0)
                rate = (current[^1].Window.Utilization - current[0].Window.Utilization) / hours;
        }
        DateTimeOffset? closesAt = latest.Utilization >= 1 ? now : rate > 0 ? now.AddHours((1 - latest.Utilization) / rate) : null;
        if (closesAt is { } c && c >= resetsAt)
            closesAt = null;
        return new(name, latest.Utilization, resetsAt, rate, closesAt);
    }

    /// <summary>A reset already past <paramref name="now"/> is rolled forward by whole window lengths until it is not (the window the provider last named has reset at least
    /// that many times with the log carrying no newer reading of it).</summary>
    private static DateTimeOffset Project(DateTimeOffset resetsAt, TimeSpan length, DateTimeOffset now)
    {
        if (resetsAt > now)
            return resetsAt;
        var behind = now - resetsAt;
        var steps = behind.Ticks / length.Ticks + 1;
        return resetsAt + length * steps;
    }

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
