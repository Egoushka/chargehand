using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Runtimes.ClaudeCode.Api;
using Xunit;

namespace Chargehand.Tests;

/// <summary>A subscription's second credential (issue #632): read from the profile and refused beside an API key. Nothing selects it yet.</summary>
public sealed class ClaudeCodeApiFallbackTests
{
    [Fact]
    public void The_profile_names_a_subscription_and_its_api_fallback()
    {
        using var dir = new TempDir();
        var profile = Profile.Load(dir.Write("p.json", """
            {"schema":"profile/v1","claude_code":{"version":"2.1.303","oauth_token_secret":"sub-token","custom_headers_secret":"sub-headers",
              "base_url":"https://gateway.example/anthropic","api_fallback_key_secret":"api-key"}}
            """));

        var cc = profile.ClaudeCode!;
        Assert.Equal(("sub-token", "sub-headers", "api-key", (string?)null), (cc.OauthTokenSecret, cc.CustomHeadersSecret, cc.ApiFallbackKeySecret, cc.ApiKeySecret));
        cc.CheckApiFallback();
    }

    [Fact]
    public void An_api_fallback_beside_an_api_key_is_refused() =>
        Assert.Equal(ErrorCode.InvalidRequest,
            Assert.Throws<ChargehandException>(() => new ClaudeCodeSettings("1", ApiKeySecret: "key", ApiFallbackKeySecret: "api").CheckApiFallback()).Code);

    [Fact]
    public void The_cli_login_takes_an_api_fallback_too() => new ClaudeCodeSettings("1", ApiFallbackKeySecret: "api").CheckApiFallback();
}
