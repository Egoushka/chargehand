using System.Net;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Economics.Api;

namespace Chargehand.Tests;

/// <summary><c>GET /v1/router</c> (router/v1): the budget router's pools and routes; owner only; off without a pools block.</summary>
public class ConsoleRouterTests
{
    private static AccountSettings Alice() => new("alice", Driven: true, AllowServerCredential: true);

    private static readonly PoolsSettings Block = new(Enabled: true, DefaultTier: "cheap", Tiers: [new("cheap", ["free/small"])],
        Routes: new Dictionary<string, string> { ["driven"] = "cheap" },
        Order: [new("free", PoolKinds.Free, ["free/small"], NoTrainingModels: ["*"])]);

    [Fact]
    public async Task A_profile_with_pools_lists_them_in_order_and_the_routes()
    {
        await using var rig = await ConsoleRig.StartAsync(profile: dir => Runs.Profile(dir) with { Pools = Block });

        var body = await ConsoleRig.Get(rig.Owner, "/v1/router");

        Assert.Equal("router/v1", body.GetProperty("contract_version").GetString());
        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.Equal("free", Assert.Single(body.GetProperty("pools").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal("cheap", Assert.Single(body.GetProperty("routes").EnumerateArray()).GetProperty("tier").GetString());
    }

    [Fact]
    public async Task A_profile_without_pools_answers_enabled_false_and_no_error()
    {
        await using var rig = await ConsoleRig.StartAsync();

        var body = await ConsoleRig.Get(rig.Owner, "/v1/router");

        Assert.False(body.GetProperty("enabled").GetBoolean());
        Assert.Empty(body.GetProperty("pools").EnumerateArray());
    }

    [Fact]
    public async Task An_account_key_is_refused()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()]);

        Assert.Equal(HttpStatusCode.Forbidden, await ConsoleRig.Status(rig.As("alice"), "/v1/router"));
    }
}
