using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Modules.Research.Api;

/// <summary>
/// The profile's <c>prompt_enhancer</c> (ADR 0040): an entry of <c>mcp_servers</c> that lists the tools <c>enhance</c> and
/// <c>feedback</c> (whetstone's contract). Unset: no enhancer, and every prompt is sent as written.
/// </summary>
/// <param name="Server">A key of <c>mcp_servers</c>.</param>
/// <param name="DeadlineMs">How long to wait for <c>enhance</c> before sending the original; null is <see cref="DefaultDeadlineMs"/>. Also the <c>deadline_ms</c> the enhancer is told.</param>
public sealed record PromptEnhancerSettings(string Server, int? DeadlineMs = null)
{
    /// <summary>The wait with no <c>deadline_ms</c> (1.5 s): the user's prompt is never much later for asking.</summary>
    public const int DefaultDeadlineMs = 1500;

    public const int MinDeadlineMs = 50;
    public const int MaxDeadlineMs = 30_000;

    public int EffectiveDeadlineMs => DeadlineMs ?? DefaultDeadlineMs;

    /// <summary>Throws an invalid_request error when <see cref="Server"/> is not one of <paramref name="servers"/> (the keys of <c>mcp_servers</c>) or the deadline is out of range.</summary>
    public void Validate(IEnumerable<string> servers)
    {
        if (string.IsNullOrEmpty(Server) || !servers.Contains(Server, StringComparer.Ordinal))
            throw new ChargehandException(ErrorCode.InvalidRequest, $"prompt_enhancer.server '{Server}' is not defined in mcp_servers",
                "Add the server to mcp_servers, or remove prompt_enhancer; docs/guide/reference.md lists the fields.");
        if (EffectiveDeadlineMs is < MinDeadlineMs or > MaxDeadlineMs)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"prompt_enhancer.deadline_ms must be {MinDeadlineMs} to {MaxDeadlineMs}",
                "Fix deadline_ms in the profile.");
    }
}
