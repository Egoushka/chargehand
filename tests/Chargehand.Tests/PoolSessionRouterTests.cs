using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Kernel;
using Chargehand.Modules.Economics.Api;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0095, D-113: the router a driven batch's tasks ask before each session. It keeps the batch's data class, so restricted data goes only to a pool whose provider does not train on it, before and after
/// a refusal, and a pool a provider refused is out for the next session.</summary>
public class PoolSessionRouterTests
{
    private static readonly PoolSettings Training = new("free", PoolKinds.Monthly, ["free/small"], TotalUsd: 10);
    private static readonly PoolSettings Private = new("private", PoolKinds.Monthly, ["paid/small", "paid/big"], TotalUsd: 10, NoTrainingModels: ["*"]);
    private static readonly PoolSettings OtherPrivate = new("other", PoolKinds.Monthly, ["paid/big"], TotalUsd: 10, NoTrainingModels: ["*"]);

    private static PoolsSettings On(params PoolSettings[] order) => new(Enabled: true, DefaultTier: "cheap",
        Tiers: [new("cheap", ["free/small", "paid/small"]), new("strong", ["paid/big"])], Order: order);

    private static PoolSessionRouter Router(TempDir dir, DataClass dataClass, params PoolSettings[] order) =>
        new(new PoolRouting(On(order), new JsonlRunLog(Path.Combine(dir.Path, "log.jsonl"))), "driven", null, null, dataClass);

    [Fact]
    public async Task Restricted_data_goes_only_to_a_pool_that_keeps_it_private_and_waits_when_that_pool_is_refused_instead_of_falling_to_one_that_may_train()
    {
        using var dir = new TempDir();
        var router = Router(dir, DataClass.Restricted, Training, Private);

        var first = await router.RouteAsync(CancellationToken.None);
        router.Refused(first.Route!, budget: true);
        var second = await router.RouteAsync(CancellationToken.None);

        Assert.Equal("private", first.Route!.Pool);
        Assert.Equal("restricted", first.Route.DataClass);
        Assert.Null(second.Route);
        Assert.Null(second.Model);
        Assert.NotNull(second.WaitUntil);                       // not the pool that may train
    }

    [Fact]
    public async Task Restricted_data_that_no_pool_keeps_private_is_refused_as_the_batch_is()
    {
        using var dir = new TempDir();
        var router = Router(dir, DataClass.Restricted, Training);

        var failure = await Assert.ThrowsAsync<ChargehandException>(() => router.RouteAsync(CancellationToken.None));

        Assert.Equal(ErrorCode.RepositoryNotAllowed, failure.Code);
    }

    [Fact]
    public async Task Public_data_drains_the_pools_in_order_and_a_refusal_moves_the_next_session_to_the_next_pool()
    {
        using var dir = new TempDir();
        var router = Router(dir, DataClass.Public, Training, Private);

        var first = await router.RouteAsync(CancellationToken.None);
        router.Refused(first.Route!, budget: false);
        var second = await router.RouteAsync(CancellationToken.None);

        Assert.Equal(("free", "free/small", "public"), (first.Route!.Pool, first.Model, first.Route.DataClass));
        Assert.Equal(("private", "paid/small", "public"), (second.Route!.Pool, second.Model, second.Route.DataClass));
        Assert.Null(second.WaitUntil);
    }

    [Fact]
    public async Task Every_pool_refused_is_a_wait_until_the_router_says_one_is_back()
    {
        using var dir = new TempDir();
        var router = Router(dir, DataClass.Public, Training, Private);

        for (var i = 0; i < 2; i++)
            router.Refused((await router.RouteAsync(CancellationToken.None)).Route!, budget: false);
        var waiting = await router.RouteAsync(CancellationToken.None);

        Assert.Null(waiting.Route);
        Assert.True(waiting.WaitUntil > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void A_task_may_move_to_another_pool_as_many_times_as_the_router_has_pools()
    {
        using var dir = new TempDir();

        Assert.Equal(3, Router(dir, DataClass.Public, Training, Private, OtherPrivate).Pools);
    }
}
