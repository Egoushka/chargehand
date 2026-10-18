using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Runtimes.ClaudeCode.Api;

namespace Chargehand.Tests;

/// <summary>A detected claude CLI runs with no claude_code block (ADR 0026): the pinned version or newer, the credential the environment holds.</summary>
public class ClaudeCodeDefaultsTests
{
    [Fact]
    public void An_api_key_alone_selects_the_api_client_mode()
    {
        var cc = ClaudeCodeSettings.Detect(item => item == "anthropic-api-key");

        Assert.Equal(("claude", ClaudeCodeSettings.MinimumVersion, "anthropic-api-key", (string?)null), (cc.Binary, cc.Version, cc.ApiKeySecret, cc.OauthTokenSecret));
    }

    [Fact]
    public void An_oauth_token_alone_selects_the_subscription_mode()
    {
        var cc = ClaudeCodeSettings.Detect(item => item == "claude-code-oauth-token");

        Assert.Equal(((string?)null, "claude-code-oauth-token"), (cc.ApiKeySecret, cc.OauthTokenSecret));
    }

    [Fact]
    public void Both_credentials_is_an_invalid_request_not_a_silent_pick()
    {
        var e = Assert.Throws<ChargehandException>(() => ClaudeCodeSettings.Detect(_ => true));
        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Contains("claude_code", e.Action);
    }

    [Fact]
    public void No_credential_selects_the_cli_login()
    {
        var cc = ClaudeCodeSettings.Detect(_ => false);

        Assert.Equal(("claude", ClaudeCodeSettings.MinimumVersion, (string?)null, (string?)null), (cc.Binary, cc.Version, cc.ApiKeySecret, cc.OauthTokenSecret));
    }

    [Fact]
    public void A_detected_cli_takes_the_pin_or_newer()
    {
        // Claude Code updates itself; an exact pin with no profile would refuse every CLI within days of a release.
        Assert.Equal(">=" + ClaudeCodeSettings.PinnedVersion, ClaudeCodeSettings.Detect(_ => false).Version);
    }
}
