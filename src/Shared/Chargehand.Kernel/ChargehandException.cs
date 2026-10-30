using System.Text.RegularExpressions;
using Chargehand.Contracts;

namespace Chargehand.Kernel;

/// <summary>A failure whose cause is known; a failed result carries its code in result/v1's error (ADR 0022).</summary>
/// <param name="action">What the user or client should do about it, e.g. the command that starts the runtime.</param>
public partial class ChargehandException(ErrorCode code, string message, string? action = null) : Exception(Scrub(message))
{
    public ErrorCode Code => code;

    public string? Action => action;

    public ResultError Error => new(code, Message, Retryable(code), action);

    /// <summary>Unchanged, the same request may succeed later.</summary>
    public static bool Retryable(ErrorCode code) =>
        code is ErrorCode.RuntimeUnavailable or ErrorCode.ProviderUnavailable or ErrorCode.RateLimited or ErrorCode.DeadlineExceeded or ErrorCode.ContainerUnavailable
            or ErrorCode.SessionStalled or ErrorCode.PrFailed;

    /// <summary>The error of any exception: its own code, a rate limit recognised by its text (Claude Code's), else internal.</summary>
    public static ResultError ErrorOf(Exception e) => e is ChargehandException c
        ? c.Error
        : new ChargehandException(RateLimited(e.Message) ? ErrorCode.RateLimited : ErrorCode.Internal, e.Message).Error;

    /// <summary>A gateway or provider rate limit, as relayed by OpenCode ("Rate limit exceeded") or Claude Code (rate_limit_error, 429).</summary>
    public static bool RateLimited(string? message) => message is not null && RateLimitText().IsMatch(message);

    /// <summary>Provider and gateway error text without keys, key aliases, bearer tokens and spend figures (<see cref="Redaction.Scrub"/>).</summary>
    public static string Scrub(string text) => Redaction.Scrub(text);

    [GeneratedRegex(@"rate[ _]limit|\b429\b", RegexOptions.IgnoreCase)]
    private static partial Regex RateLimitText();
}
