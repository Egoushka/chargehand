using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Runtimes.ClaudeCode.Api;

/// <summary>Claude Code CLI as the worker runtime (ADR 0020); used instead of OpenCode when set. At most one
/// credential: an API key (per-token billing) or a subscription OAuth token from <c>claude setup-token</c>; with
/// neither, the CLI's own signed-in login. BaseUrl
/// routes the workers through a gateway that speaks the Anthropic API; unset, they call Anthropic directly.</summary>
public sealed record ClaudeCodeSettings(string Version, string? ApiKeySecret = null, string? OauthTokenSecret = null, string Binary = "claude",
    string? BaseUrl = null)
{
    /// <summary>The CLI version the adapter's event mapping was verified against (ADR 0020).</summary>
    public const string PinnedVersion = "2.1.283";

    /// <summary>
    /// No claude_code block (ADR 0026): claude from PATH at <see cref="PinnedVersion"/>, and the one credential the
    /// secrets chain resolves under the names the CLI itself reads (ANTHROPIC_API_KEY, CLAUDE_CODE_OAUTH_TOKEN with the
    /// default env source). Both is an error; neither leaves the CLI on its own signed-in login (ADR 0020).
    /// </summary>
    public static ClaudeCodeSettings Detect(Func<string, bool> resolves) =>
        (resolves("anthropic-api-key"), resolves("claude-code-oauth-token")) switch
        {
            (true, false) => new(PinnedVersion, ApiKeySecret: "anthropic-api-key"),
            (false, true) => new(PinnedVersion, OauthTokenSecret: "claude-code-oauth-token"),
            (true, true) => throw new ChargehandException(ErrorCode.InvalidRequest, "both ANTHROPIC_API_KEY and CLAUDE_CODE_OAUTH_TOKEN are set",
                "Unset one, or add claude_code to the profile naming one of api_key_secret and oauth_token_secret."),
            _ => new(PinnedVersion),
        };
}
