using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Runtimes.Acp.Api;

namespace Chargehand.Runtimes.Acp.Tests;

public class LaunchEnvironmentTests
{
    private static readonly AcpAgentSpec Agent = new("codex", "Codex", "npx", ["-y", "pkg@1"], "1", ApiKeyEnv: "OPENAI_API_KEY", BaseUrlEnv: "OPENAI_BASE_URL",
        Env: new Dictionary<string, string> { ["NO_COLOR"] = "1" });

    private static string Secrets(string item) => item == "openai-key" ? "sk-test" : throw new InvalidOperationException($"secret '{item}' does not resolve");

    [Fact]
    public void The_environment_is_the_fixed_variables_the_key_and_the_gateway_and_nothing_else()
    {
        var env = AcpLaunchEnvironment.Build(Agent, new AcpAgentCredential("openai-key", "https://gateway.example/v1"), Secrets);

        Assert.Equal(
            [("NO_COLOR", "1"), ("OPENAI_API_KEY", "sk-test"), ("OPENAI_BASE_URL", "https://gateway.example/v1")],
            env.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)));
    }

    [Fact]
    public void Without_a_gateway_the_base_url_variable_is_not_set()
    {
        var env = AcpLaunchEnvironment.Build(Agent, new AcpAgentCredential("openai-key"), Secrets);

        Assert.DoesNotContain("OPENAI_BASE_URL", env.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_agent_with_no_key_secret_is_refused_so_no_login_can_stand_in(string? item)
    {
        var e = Assert.Throws<ChargehandException>(() => AcpLaunchEnvironment.Build(Agent, item is null ? null : new AcpAgentCredential(item), Secrets));

        Assert.Equal(ErrorCode.CredentialUnavailable, e.Code);
        Assert.Contains("acp.agents.codex.api_key_secret", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_that_does_not_resolve_is_refused_naming_the_item_not_the_value()
    {
        var e = Assert.Throws<ChargehandException>(() => AcpLaunchEnvironment.Build(Agent, new AcpAgentCredential("missing"), Secrets));

        Assert.Equal(ErrorCode.CredentialUnavailable, e.Code);
        Assert.Contains("missing", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gateway_for_an_agent_that_cannot_be_pointed_at_one_is_refused()
    {
        var fixedEndpoint = Agent with { BaseUrlEnv = null };

        var e = Assert.Throws<ChargehandException>(() => AcpLaunchEnvironment.Build(fixedEndpoint, new AcpAgentCredential("openai-key", "https://gateway.example/v1"), Secrets));

        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
    }
}
