using System.Globalization;
using System.Text.RegularExpressions;
using Chargehand.Kernel;

namespace Chargehand.Modules.Sessions.Api;

/// <summary>What a session's last result says about a usage or rate limit (ADR 0045).</summary>
public static partial class UsageLimit
{
    /// <summary>How long the scheduler holds new starts when a limit names no reset time.</summary>
    public static readonly TimeSpan DefaultBackoff = TimeSpan.FromMinutes(15);

    /// <summary>A session's error text names a usage or rate limit. Stricter than <see cref="ChargehandException.RateLimited"/>, which other callers share: 429 counts only as an
    /// HTTP status ("API Error: 429", "status 429", "429 Too Many Requests"), never as a line number in a stack trace or a compiler message.</summary>
    public static bool Matches(string? text) => text is not null && LimitText().IsMatch(text);

    /// <summary>When the limit lifts: the Unix seconds Claude Code prints after a pipe ("Claude AI usage limit reached|1760281200"), else the local clock time 2.1.303 prints
    /// ("You've hit your session limit · resets 4:41pm (Europe/Kiev)", optionally after a date such as "Nov 3,") in that zone, the next one after <paramref name="now"/>. Null
    /// when the text has neither, or names a zone this host does not know: the caller waits the default backoff.</summary>
    public static DateTimeOffset? ResetsAt(string? text, DateTimeOffset now)
    {
        if (text is null)
            return null;
        if (EpochAfterPipe().Match(text) is { Success: true } m)
            return DateTimeOffset.FromUnixTimeSeconds(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        return ClockReset().Match(text) is { Success: true } c ? LocalReset(c, now) : null;
    }

    private static DateTimeOffset? LocalReset(Match m, DateTimeOffset now)
    {
        if (Zone(m.Groups["zone"].Value) is not { } zone)
            return null;
        var hour = int.Parse(m.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var minute = m.Groups["minute"].Success ? int.Parse(m.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
        if (hour is < 1 or > 12 || minute > 59)
            return null;
        hour = hour % 12 + (m.Groups["ampm"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        var dated = m.Groups["month"].Success;
        DateTime day;
        if (!dated)
            day = local.Date;
        else if (!DateTime.TryParseExact($"{m.Groups["month"].Value} {m.Groups["day"].Value} {local.Year}", ["MMM d yyyy", "MMMM d yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
            return null;
        var at = day.AddHours(hour).AddMinutes(minute);
        if (at <= local)
            at = dated ? at.AddYears(1) : at.AddDays(1);
        return new DateTimeOffset(at, zone.GetUtcOffset(at)).ToUniversalTime();
    }

    /// <summary>The zone by its IANA id, or by the other name of a renamed one: Claude Code prints "Europe/Kiev", which newer tzdata ships only in tzdata-legacy.</summary>
    private static TimeZoneInfo? Zone(string id)
    {
        foreach (var name in Renamed.TryGetValue(id, out var other) ? [id, other] : new[] { id })
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(name);
            }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        return null;
    }

    private static readonly Dictionary<string, string> Renamed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Europe/Kiev"] = "Europe/Kyiv",
        ["Europe/Kyiv"] = "Europe/Kiev",
    };

    [GeneratedRegex(@"usage[ _]limit|rate[ _]limit|(?:session|weekly) limit|too many requests|\b(?:status|http|error)\b[\s:=]*429\b", RegexOptions.IgnoreCase)]
    private static partial Regex LimitText();

    [GeneratedRegex(@"\|\s*(\d{10})\b")]
    private static partial Regex EpochAfterPipe();

    [GeneratedRegex(@"\bresets\s+(?:(?<month>[A-Za-z]{3,9})\s+(?<day>\d{1,2}),?\s+(?:at\s+)?)?(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<ampm>[ap]m)\s*\((?<zone>[A-Za-z_]+(?:/[A-Za-z_+-]+)+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ClockReset();
}
