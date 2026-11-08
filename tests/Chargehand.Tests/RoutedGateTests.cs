using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Economics.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>A routed batch's start gate (ADR 0095): a spent budget takes the batch's pool out of the router, and halts every start only when no other pool can take the batch's preset.</summary>
public class RoutedGateTests
{
    private static readonly RunRoute Subscription = new("subscription", "strong", "paid/big", "public");
    private static readonly DateTimeOffset Resets = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    private static PoolsSettings Pools(params PoolSettings[] order) => new(Enabled: true, DefaultTier: "strong",
        Tiers: [new("strong", ["paid/big"])], Order: order);

    private static PoolSettings Pool(string name) => new(name, PoolKinds.Monthly, ["paid/big"], TotalUsd: 10, NoTrainingModels: ["*"]);

    private static (RoutedGate Gate, PoolRouting Routing, FakeGate Inner) Build(params PoolSettings[] order)
    {
        var routing = new PoolRouting(Pools(order), new JsonlRunLog(Path.Combine(Path.GetTempPath(), "routed-gate-never-read.jsonl")));
        var inner = new FakeGate();
        return (new RoutedGate(inner, routing, Subscription, "driven", null, DataClass.Public), routing, inner);
    }

    [Fact]
    public void A_spent_budget_with_another_pool_to_take_over_halts_nothing_and_the_next_start_falls_through()
    {
        var (gate, routing, inner) = Build(Pool("subscription"), Pool("credit"));

        gate.BudgetExhausted(Resets);

        Assert.Empty(inner.Halts);
        Assert.True(routing.CanRoute("driven", null, DataClass.Public));
        Assert.True(routing.Ended(new RunRoute("credit", "strong", "paid/big"), new ResultError(ErrorCode.RateLimited, "402 Payment Required", true)));
        Assert.False(routing.CanRoute("driven", null, DataClass.Public));
    }

    [Fact]
    public void A_spent_budget_on_the_only_pool_halts_every_start_until_its_reset()
    {
        var (gate, routing, inner) = Build(Pool("subscription"));

        gate.BudgetExhausted(Resets);

        Assert.Equal([Resets], inner.Halts);
        Assert.False(routing.CanRoute("driven", null, DataClass.Public));
    }

    [Fact]
    public void Everything_else_goes_to_the_gate_the_batches_share()
    {
        var (gate, _, inner) = Build(Pool("subscription"));
        var retryAfter = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

        Assert.True(gate.TryEnter());
        gate.Exit();
        gate.RateLimited(retryAfter);

        Assert.Equal(["enter", "exit"], inner.Calls);
        Assert.Equal([retryAfter], inner.Limits);
        Assert.Equal(inner.HaltedUntil, gate.HaltedUntil);
    }

    private sealed class FakeGate : IStartGate
    {
        public List<string> Calls { get; } = [];

        public List<DateTimeOffset> Halts { get; } = [];

        public List<DateTimeOffset?> Limits { get; } = [];

        public DateTimeOffset? HaltedUntil => new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);

        public bool TryEnter()
        {
            Calls.Add("enter");
            return true;
        }

        public void Exit() => Calls.Add("exit");

        public void RateLimited(DateTimeOffset? retryAfter) => Limits.Add(retryAfter);

        public void BudgetExhausted(DateTimeOffset resetsAt) => Halts.Add(resetsAt);
    }
}
