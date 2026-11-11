using Chargehand.Modules.Economics.Api;

namespace Chargehand.Modules.Economics.Tests;

/// <summary><c>router/v1</c>: the pools in drain order with balance, allowance and spend, and the tier each route resolves to.</summary>
public class RouterReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly PoolsSettings Block = new(Enabled: true, DefaultTier: "cheap",
        Tiers: [new("cheap", ["free/small"]), new("strong", ["paid/big"])],
        Routes: new Dictionary<string, string> { ["review"] = "strong", ["driven"] = "cheap" },
        Order: [new("free", PoolKinds.Free, ["free/small"]), new("bought", PoolKinds.Fixed, ["paid/big"], TotalUsd: 30, Start: new DateOnly(2026, 11, 1), SpreadDays: 30, NoTrainingModels: ["*"])]);

    [Fact]
    public void Pools_come_in_drain_order_with_balance_allowance_and_spend()
    {
        var spend = new[] { new PoolSpend("bought", Now.AddDays(-2), 6m), new PoolSpend("bought", Now.AddHours(-1), 1m), new PoolSpend("free", Now.AddHours(-1), 0m) };

        var report = RouterReportBuilder.Build(Block, spend, Now);

        Assert.Equal(("router/v1", true), (report.ContractVersion, report.Enabled));
        Assert.Equal(["free", "bought"], report.Pools.Select(p => p.Name));
        var bought = report.Pools[1];
        Assert.Equal((23m, 7m, 1m), (bought.RemainingUsd, bought.SpentPeriodUsd, bought.SpentTodayUsd));
        Assert.True(bought.HasRoom);
        Assert.NotNull(bought.AllowanceUsd);
    }

    [Fact]
    public void Routes_resolve_to_a_tier_and_its_models_sorted_by_key()
    {
        var report = RouterReportBuilder.Build(Block, [], Now);

        Assert.Equal([("driven", "cheap"), ("review", "strong")], report.Routes.Select(r => (r.Key, r.Tier)));
        Assert.Equal(["paid/big"], report.Routes[1].Models);
        Assert.Equal("cheap", report.DefaultTier);
    }

    [Fact]
    public void A_profile_without_pools_or_with_a_disabled_or_invalid_block_reports_the_router_off()
    {
        foreach (var settings in new PoolsSettings?[] { null, new PoolsSettings(), Block with { Enabled = false }, Block with { Order = [] } })
        {
            var report = RouterReportBuilder.Build(settings, [], Now);

            Assert.False(report.Enabled);
            Assert.Empty(report.Pools);
            Assert.Empty(report.Routes);
        }
    }
}
