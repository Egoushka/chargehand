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

    /// <summary>The reset time Claude Code prints after a pipe ("Claude AI usage limit reached|1760281200", Unix seconds); null when the text has none in that form. Other
    /// wordings ("resets 3pm") are not parsed: they get the default backoff.</summary>
    public static DateTimeOffset? ResetsAt(string? text)
    {
        if (text is null || EpochAfterPipe().Match(text) is not { Success: true } m)
            return null;
        return DateTimeOffset.FromUnixTimeSeconds(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"usage[ _]limit|rate[ _]limit|too many requests|\b(?:status|http|error)\b[\s:=]*429\b", RegexOptions.IgnoreCase)]
    private static partial Regex LimitText();

    [GeneratedRegex(@"\|\s*(\d{10})\b")]
    private static partial Regex EpochAfterPipe();
}
