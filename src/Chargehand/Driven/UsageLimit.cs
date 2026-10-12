using System.Globalization;
using System.Text.RegularExpressions;

namespace Chargehand.Driven;

/// <summary>What a session's last result says about a usage or rate limit (ADR 0045).</summary>
public static partial class UsageLimit
{
    /// <summary>How long the scheduler holds new starts when a limit names no reset time.</summary>
    public static readonly TimeSpan DefaultBackoff = TimeSpan.FromMinutes(15);

    /// <summary>The reset time Claude Code prints after a pipe ("Claude AI usage limit reached|1760281200", Unix seconds); null when the text has none in that form. Other
    /// wordings ("resets 3pm") are not parsed: they get the default backoff.</summary>
    public static DateTimeOffset? ResetsAt(string? text)
    {
        if (text is null || EpochAfterPipe().Match(text) is not { Success: true } m)
            return null;
        return DateTimeOffset.FromUnixTimeSeconds(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"\|\s*(\d{10})\b")]
    private static partial Regex EpochAfterPipe();
}
