using System.Text.RegularExpressions;
using Chargehand.Contracts;

namespace Chargehand;

/// <summary>A failure whose cause is known; a failed result carries its code in result/v1's error (ADR 0022).</summary>
/// <param name="action">What the user or client should do about it, e.g. the command that starts the runtime.</param>
public partial class ChargehandException(ErrorCode code, string message, string? action = null) : Exception(Scrub(message))
{
    public ErrorCode Code => code;

    public string? Action => action;

    public ResultError Error => new(code, Message, Retryable(code), action);

    /// <summary>Unchanged, the same request may succeed later.</summary>
    public static bool Retryable(ErrorCode code) =>
        code is ErrorCode.RuntimeUnavailable or ErrorCode.ProviderUnavailable or ErrorCode.RateLimited or ErrorCode.DeadlineExceeded or ErrorCode.ContainerUnavailable;

    /// <summary>The error of any exception: its own code, a rate limit recognised by its text (Claude Code's), else internal.</summary>
    public static ResultError ErrorOf(Exception e) => e is ChargehandException c
        ? c.Error
        : new ChargehandException(RateLimited(e.Message) ? ErrorCode.RateLimited : ErrorCode.Internal, e.Message).Error;

    /// <summary>A gateway or provider rate limit, as relayed by OpenCode ("Rate limit exceeded") or Claude Code (rate_limit_error, 429).</summary>
    public static bool RateLimited(string? message) => message is not null && RateLimitText().IsMatch(message);

    /// <summary>
    /// Provider and gateway error text without what must not leave the machine: keys, key aliases, bearer tokens and
    /// spend figures. Every exception message that can reach result/v1 or the run log passes through here.
    /// </summary>
    public static string Scrub(string text)
    {
        text = ApiKey().Replace(text, "sk-[redacted]");
        text = AuthScheme().Replace(text, "${scheme} [redacted]");
        text = Named().Replace(text, "${k}[redacted]");
        return Spend().Replace(text, "${k}[redacted]");
    }

    [GeneratedRegex(@"rate[ _]limit|\b429\b", RegexOptions.IgnoreCase)]
    private static partial Regex RateLimitText();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])sk-[^\s)""',]+")]
    private static partial Regex ApiKey();

    /// <summary>Key-ish identifiers: bare or compound (apiKey, key_hash, team_member, user_id, organization…).
    /// The value is excluded when it's actually a bearer/basic scheme word — <see cref="AuthScheme"/> owns that shape.</summary>
    [GeneratedRegex(@"(?<k>\b(?:\w*(?:key|alias|token|hash|authorization|organization)\w*|team_?member|team_?alias|user_?id|end_?user)\b["")]*\s*[:=]\s*[""(]*)(?!(?:bearer|basic)\b)[^\s,)""]+", RegexOptions.IgnoreCase)]
    private static partial Regex Named();

    [GeneratedRegex(@"\b(?<scheme>bearer|basic)\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthScheme();

    [GeneratedRegex(@"(?<k>\b(?:current cost|max budget|spend|budget)\s*[=:]\s*)\$?[0-9]+(?:\.[0-9]+)?(?:[eE][-+]?[0-9]+)?", RegexOptions.IgnoreCase)]
    private static partial Regex Spend();
}
