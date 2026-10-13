using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Kernel;
using Chargehand.Modules.Handover.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0046: which repositories the integrator may act on. Off by default; the profile lists the rest; <c>always_ask</c> wins.</summary>
public class IntegratorPolicyTests
{
    private static readonly RemoteRepository Mine = new("owner", "app");
    private static readonly RemoteRepository Servers = new("owner", "servers");

    private static DrivenIntegrator Config(Dictionary<string, string>? repositories = null, string[]? alwaysAsk = null, DrivenCiFix? ciFix = null) =>
        new(1, 2, "app-key", repositories, alwaysAsk, ciFix);

    [Fact]
    public void A_repository_the_profile_does_not_list_is_off()
    {
        var policy = new IntegratorPolicy(Config(new() { ["owner/app"] = "merge" }));
        Assert.Equal(IntegrationMode.Off, policy.Resolve(new RemoteRepository("owner", "other")));
        Assert.Equal(IntegrationMode.Off, new IntegratorPolicy(Config()).Resolve(Mine));
    }

    [Theory]
    [InlineData("merge", IntegrationMode.Merge)]
    [InlineData("ask", IntegrationMode.Ask)]
    public void A_listed_repository_gets_its_mode(string value, IntegrationMode expected) =>
        Assert.Equal(expected, new IntegratorPolicy(Config(new() { ["owner/app"] = value })).Resolve(Mine));

    [Fact]
    public void Always_ask_wins_over_merge_and_over_a_repository_that_is_not_listed()
    {
        var policy = new IntegratorPolicy(Config(new() { ["owner/app"] = "merge", ["owner/servers"] = "merge" }, ["owner/servers"]));
        Assert.Equal(IntegrationMode.Ask, policy.Resolve(Servers));
        Assert.Equal(IntegrationMode.Merge, policy.Resolve(Mine));
        Assert.Equal(IntegrationMode.Ask, new IntegratorPolicy(Config(alwaysAsk: ["owner/servers"])).Resolve(Servers));
    }

    [Fact]
    public void Names_are_compared_without_regard_to_case()
    {
        var policy = new IntegratorPolicy(Config(new() { ["Owner/App"] = "merge" }, ["OWNER/Servers"]));
        Assert.Equal(IntegrationMode.Merge, policy.Resolve(Mine));
        Assert.Equal(IntegrationMode.Ask, policy.Resolve(Servers));
    }

    [Fact]
    public void The_profile_block_loads_and_a_profile_without_it_has_no_integrator()
    {
        using var dir = new TempDir();
        var with = Profile.Load(dir.Write("with.json", """
            {"schema":"profile/v1","driven":{"enabled":true,"integrator":{"app_id":7,"installation_id":8,"private_key_secret":"app-key",
              "repositories":{"o/r":"merge"},"always_ask":["o/servers"],"ci_fix":{"max_attempts":2,"max_turns":10}}}}
            """)).Driven!.Integrator!;
        Assert.Equal((7L, 8L, "app-key"), (with.AppId, with.InstallationId, with.PrivateKeySecret));
        Assert.Equal("merge", with.Repositories!["o/r"]);
        Assert.Equal(["o/servers"], with.AlwaysAsk);
        Assert.Equal((2, 10, 15, 20), (with.CiFix!.Attempts, with.CiFix.Turns, with.CiFix.Minutes, with.CiFix.Wait));

        Assert.Null(Profile.Load(dir.Write("without.json", """{"schema":"profile/v1","driven":{"enabled":true}}""")).Driven!.Integrator);
        Assert.Null(Profile.Load(dir.Write("none.json", """{"schema":"profile/v1"}""")).Driven);
    }

    [Theory]
    [InlineData("""{"app_id":0,"installation_id":8,"private_key_secret":"k"}""", "app_id")]
    [InlineData("""{"app_id":7,"installation_id":0,"private_key_secret":"k"}""", "installation_id")]
    [InlineData("""{"app_id":7,"installation_id":8,"private_key_secret":""}""", "private_key_secret")]
    [InlineData("""{"app_id":7,"installation_id":8,"private_key_secret":"k","repositories":{"o/r":"yes"}}""", "repositories.o/r")]
    [InlineData("""{"app_id":7,"installation_id":8,"private_key_secret":"k","repositories":{"just-a-name":"merge"}}""", "owner/repo")]
    [InlineData("""{"app_id":7,"installation_id":8,"private_key_secret":"k","always_ask":["x"]}""", "always_ask")]
    [InlineData("""{"app_id":7,"installation_id":8,"private_key_secret":"k","ci_fix":{"max_attempts":4}}""", "max_attempts")]
    [InlineData("""{"app_id":7,"installation_id":8,"private_key_secret":"k","ci_fix":{"max_turns":0}}""", "max_turns")]
    public void A_value_that_cannot_work_is_refused_when_the_profile_loads(string block, string named)
    {
        using var dir = new TempDir();
        var path = dir.Write("p.json", $$$"""{"schema":"profile/v1","driven":{"enabled":true,"integrator":{{{block}}}}}""");
        var e = Assert.Throws<ChargehandException>(() => Profile.Load(path));
        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Contains(named, e.Message);
    }
}
