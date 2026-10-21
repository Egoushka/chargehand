using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Runtimes.ClaudeCode.Api;

/// <summary>Claude Code CLI as the worker runtime (ADR 0020); used instead of OpenCode when set. At most one
/// credential: an API key (per-token billing) or a subscription OAuth token from <c>claude setup-token</c>; with
/// neither, the CLI's own signed-in login. BaseUrl
/// routes the workers through a gateway that speaks the Anthropic API; unset, they call Anthropic directly. CustomHeadersSecret names a
/// secret holding header lines (<c>Name: value</c>, one per line) the workers send to that gateway as <c>ANTHROPIC_CUSTOM_HEADERS</c>: a
/// gateway's virtual key travels in one, so the value is a model credential.</summary>
public sealed record ClaudeCodeSettings(string Version, string? ApiKeySecret = null, string? OauthTokenSecret = null, string Binary = "claude",
    string? BaseUrl = null, string? CustomHeadersSecret = null)
{
    /// <summary>The CLI version the adapter's event mapping was verified against (ADR 0020).</summary>
    public const string PinnedVersion = "2.1.283";

    /// <summary>The pin with no profile block: <see cref="PinnedVersion"/> or newer of its major, since the CLI updates itself. A profile's
    /// <c>version</c> may be exact or take the same <c>&gt;=</c> form.</summary>
    public const string MinimumVersion = ">=" + PinnedVersion;

    /// <summary>The header lines to hand the CLI as <c>ANTHROPIC_CUSTOM_HEADERS</c>, from the secret's value. Throws <see cref="ChargehandException"/> without a <see cref="BaseUrl"/>
    /// (the headers would go to Anthropic itself) or for a line that is not <c>Name: value</c>.</summary>
    public string CustomHeaders(string secretValue)
    {
        if (BaseUrl is null)
            throw new ChargehandException(ErrorCode.InvalidRequest, "claude_code.custom_headers_secret needs claude_code.base_url: the headers are for the gateway, not Anthropic",
                "Set claude_code.base_url, or remove custom_headers_secret.");
        var lines = secretValue.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || lines.Any(l => l.IndexOf(':') is var i && (i < 1 || i == l.Length - 1 || l[..i].Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))))
            throw new ChargehandException(ErrorCode.InvalidRequest, "claude_code.custom_headers_secret must hold lines of 'Name: value'", "Fix the secret's value; one header per line.");
        return string.Join('\n', lines);
    }

    /// <summary>The value of each header line in <paramref name="secretValue"/>: what a transcript scan looks for, since the whole line never appears verbatim in a session's output.</summary>
    public static IEnumerable<string> HeaderValues(string secretValue) =>
        secretValue.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l[(l.IndexOf(':') + 1)..].Trim()).Where(v => v.Length > 0);

    /// <summary>
    /// No claude_code block (ADR 0026): claude from PATH at <see cref="MinimumVersion"/>, and the one credential the
    /// secrets chain resolves under the names the CLI itself reads (ANTHROPIC_API_KEY, CLAUDE_CODE_OAUTH_TOKEN with the
    /// default env source). Both is an error; neither leaves the CLI on its own signed-in login (ADR 0020).
    /// </summary>
    public static ClaudeCodeSettings Detect(Func<string, bool> resolves) =>
        (resolves("anthropic-api-key"), resolves("claude-code-oauth-token")) switch
        {
            (true, false) => new(MinimumVersion, ApiKeySecret: "anthropic-api-key"),
            (false, true) => new(MinimumVersion, OauthTokenSecret: "claude-code-oauth-token"),
            (true, true) => throw new ChargehandException(ErrorCode.InvalidRequest, "both ANTHROPIC_API_KEY and CLAUDE_CODE_OAUTH_TOKEN are set",
                "Unset one, or add claude_code to the profile naming one of api_key_secret and oauth_token_secret."),
            _ => new(MinimumVersion),
        };
}
