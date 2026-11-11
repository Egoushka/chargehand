using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Kernel;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>A runner's route takes the model credential by the variable it is stored under, so another entry in the model environment does not break the batch.</summary>
public sealed class SiteRouteCredentialTests
{
    [Fact]
    public void A_model_environment_with_a_second_entry_still_gives_the_subscription_token_to_the_exchange()
    {
        var environment = new Dictionary<string, string>(DrivenRun.ModelEnvironment(false, "oauth-token")) { ["CLAUDE_CODE_SUBAGENT_MODEL"] = "p/small" };

        var route = Assert.Single(SiteRoute.Of(Setup(priced: false, environment), "run-token-key"));

        Assert.Equal("oauth-token", route.Exchange!.Credential);
    }

    [Fact]
    public void A_model_environment_with_a_second_entry_still_routes_a_priced_key()
    {
        var environment = new Dictionary<string, string>(DrivenRun.ModelEnvironment(true, "api-key")) { ["CLAUDE_CODE_SUBAGENT_MODEL"] = "p/small" };

        var route = Assert.Single(SiteRoute.Of(Setup(priced: true, environment), null));

        Assert.Null(route.Exchange);
    }

    [Fact]
    public void A_model_environment_without_the_credential_names_the_missing_variable()
    {
        var environment = new Dictionary<string, string> { ["CLAUDE_CODE_SUBAGENT_MODEL"] = "p/small" };

        var error = Assert.Throws<ChargehandException>(() => SiteRoute.Of(Setup(priced: false, environment), "run-token-key"));

        Assert.Equal(ErrorCode.CredentialUnavailable, error.Code);
        Assert.Contains("CLAUDE_CODE_OAUTH_TOKEN", error.Message);
    }

    private static BatchSetup Setup(bool priced, IReadOnlyDictionary<string, string> environment) =>
        new(null!, new DrivenSettings(true), null!, "registry.test/session:latest", new DrivenPreset(), "p/model", null, priced, null!, null, environment,
            [new RunnerSite(new DrivenRunnerEntry("box", "http://box.internal:4310", "runner-key-item"), null!, null!, null!)]);
}
