using System.Globalization;
using System.Text.RegularExpressions;

namespace Chargehand.Modules.Sessions.Api;

/// <summary>What a model gateway's refusal in a session's result text means for the batches that share it (issue #578): a spent budget holds every start until its reset, a plain rate limit
/// only shrinks the pool.</summary>
public static partial class GatewayRefusal
{
    /// <summary>The <see cref="SessionOutcome.Reason"/> of a session that the gateway refused over a spend budget.</summary>
    public const string BudgetReason = "budget_exhausted";

    /// <summary>402, or a usage or rate limit (429) whose text names a budget.</summary>
    public static bool IsBudget(string? text) =>
        text is not null && (PaymentRequired().IsMatch(text) || (UsageLimit.Matches(text) && text.Contains("budget", StringComparison.OrdinalIgnoreCase)));

    /// <summary>When the refusal lifts: a budget's own reset, else the next UTC midnight; a rate limit's <c>retry-after</c> (seconds, or a time), else Claude Code's reset epoch, else null.</summary>
    public static DateTimeOffset? ResetsAt(string? text, DateTimeOffset now)
    {
        if (text is null)
            return null;
        var named = Reset().Match(text) is { Success: true } r && DateTimeOffset.TryParse(r.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : RetryAfter().Match(text) is { Success: true } m && double.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var seconds) ? now.AddSeconds(seconds)
            : UsageLimit.ResetsAt(text);
        return IsBudget(text) ? (named is { } t && t > now ? t : NextUtcDay(now)) : named;
    }

    public static DateTimeOffset NextUtcDay(DateTimeOffset now) => new(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);

    [GeneratedRegex(@"\b(?:status|http|error)\b[\s:=]*402\b|\b402\b[\s:]*(?:payment required|budget)|payment required|budget has been exceeded", RegexOptions.IgnoreCase)]
    private static partial Regex PaymentRequired();

    [GeneratedRegex(@"retry[- _]after""?[\s:=]+""?(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex RetryAfter();

    [GeneratedRegex(@"(?:budget[_ ]reset[_ ]at|resets[_ ]at)""?[\s:=]+""?(\d{4}-\d{2}-\d{2}[T ][0-9:.]+Z?)", RegexOptions.IgnoreCase)]
    private static partial Regex Reset();
}
