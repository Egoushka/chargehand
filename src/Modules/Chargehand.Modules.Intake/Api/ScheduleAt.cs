using System.Globalization;

namespace Chargehand.Modules.Intake.Api;

/// <summary>When a schedule entry is due (ADR 0068): <c>daily HH:MM</c>, <c>weekly &lt;mon..sun&gt; HH:MM</c> or <c>monthly &lt;1-28&gt; HH:MM</c>, all in UTC. Days of the month stop at 28 so every month has one.
/// Not cron: one occurrence per day, week or month.</summary>
public readonly record struct ScheduleAt
{
    private static readonly string[] Days = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    private enum Period { Daily, Weekly, Monthly }

    private Period Every { get; init; }

    private int Day { get; init; }

    private int Hour { get; init; }

    private int Minute { get; init; }

    /// <summary>Reads the syntax; <paramref name="problem"/> says what is wrong when it returns false.</summary>
    public static bool TryParse(string? text, out ScheduleAt at, out string? problem)
    {
        at = default;
        problem = null;
        var parts = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 3)
            return Fail(out problem, "must be 'daily HH:MM', 'weekly <mon..sun> HH:MM' or 'monthly <1-28> HH:MM' (UTC)");
        var every = parts[0].ToLowerInvariant();
        var wantsDay = every is "weekly" or "monthly";
        if (every is not ("daily" or "weekly" or "monthly") || parts.Length != (wantsDay ? 3 : 2))
            return Fail(out problem, "must be 'daily HH:MM', 'weekly <mon..sun> HH:MM' or 'monthly <1-28> HH:MM' (UTC)");
        var day = 0;
        if (wantsDay && !TryDay(every, parts[1], out day))
            return Fail(out problem, every == "weekly" ? $"weekday '{parts[1]}' must be mon, tue, wed, thu, fri, sat or sun" : $"day of the month '{parts[1]}' must be 1 to 28");
        if (!TryTime(parts[^1], out var hour, out var minute))
            return Fail(out problem, $"time '{parts[^1]}' must be HH:MM, 00:00 to 23:59");
        at = new ScheduleAt { Every = Enum.Parse<Period>(every, true), Day = day, Hour = hour, Minute = minute };
        return true;
    }

    /// <summary>The first occurrence strictly after <paramref name="after"/>.</summary>
    public DateTimeOffset Next(DateTimeOffset after)
    {
        var from = after.UtcDateTime;
        var day = new DateTime(from.Year, from.Month, from.Day, Hour, Minute, 0, DateTimeKind.Utc);
        if (Every == Period.Monthly)
        {
            var month = new DateTime(from.Year, from.Month, Day, Hour, Minute, 0, DateTimeKind.Utc);
            return new DateTimeOffset(month > from ? month : month.AddMonths(1));
        }
        while (day <= from || (Every == Period.Weekly && (int)day.DayOfWeek != Day))
            day = day.AddDays(1);
        return new DateTimeOffset(day);
    }

    private static bool TryTime(string text, out int hour, out int minute)
    {
        (hour, minute) = (0, 0);
        var time = text.Split(':');
        return time.Length == 2 && time[0].Length == 2 && time[1].Length == 2
            && int.TryParse(time[0], NumberStyles.None, CultureInfo.InvariantCulture, out hour) && hour <= 23
            && int.TryParse(time[1], NumberStyles.None, CultureInfo.InvariantCulture, out minute) && minute <= 59;
    }

    private static bool TryDay(string every, string text, out int day)
    {
        day = Array.IndexOf(Days, text.ToLowerInvariant());
        if (every == "weekly")
            return day >= 0;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out day) && day is >= 1 and <= 28;
    }

    private static bool Fail(out string? problem, string text)
    {
        problem = text;
        return false;
    }
}
