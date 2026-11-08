using Chargehand.Contracts;
using Chargehand.Modules.Economics.Api;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Modules.Economics.Tests;

/// <summary>The router as the call sites use it (ADR 0095): off changes nothing, on names the model and who pays, reads spend back from routed run records, and queues on a wait.</summary>
public class PoolRoutingTests
{
    private static readonly PoolSettings Free = new("free", PoolKinds.Free, ["free/small"]);
    private static readonly PoolSettings Credit = new("credit", PoolKinds.Monthly, ["paid/small", "paid/big"], TotalUsd: 30);

    private static PoolsSettings On(params PoolSettings[] order) => new(Enabled: true, DefaultTier: "cheap",
        Tiers: [new("cheap", ["free/small", "paid/small"]), new("strong", ["paid/big"])],
        Routes: new Dictionary<string, string> { ["driven/L"] = "strong", ["review"] = "strong" }, Order: order);

    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static PoolRouting Routing(PoolsSettings? settings, FakeLog log) => new(settings, log, new Fixed(Now));

    private static RunRecord Routed(string pool, decimal usd, DateTimeOffset finished, ErrorCode? error = null)
    {
        var (_, run) = Fixtures.Run($"run-{pool}-{usd}", "driven", "owner/repo", usd, 0);
        return run with { Finished = finished, Route = new RunRoute(pool, "cheap", "paid/small"), Result = error is { } code ? run.Result with { Error = new ResultError(code, "limited", true) } : run.Result };
    }

    [Fact]
    public async Task Off_keeps_the_callers_model_and_reads_nothing()
    {
        var log = new FakeLog();
        var routing = Routing(On(Free) with { Enabled = false }, log);

        var routed = await routing.RouteAsync("driven", "L", "anthropic/claude-sonnet-5", CancellationToken.None);

        Assert.Equal(new Routed("anthropic/claude-sonnet-5", null), routed);
        Assert.False(routing.On);
        Assert.Equal(0, log.Reads);
        Assert.Null(await Routing(null, log).WaitUntilAsync("driven", null, CancellationToken.None));
    }

    [Fact]
    public async Task On_names_the_routers_model_and_the_pool_that_pays_whatever_the_caller_resolved()
    {
        var routing = Routing(On(Free, Credit), new FakeLog());

        Assert.Equal(new Routed("free/small", new RunRoute("free", "cheap", "free/small")), await routing.RouteAsync("code", null, null, CancellationToken.None));
        Assert.Equal(new Routed("paid/big", new RunRoute("credit", "strong", "paid/big")), await routing.RouteAsync("driven", "L", "provider/strong", CancellationToken.None));
    }

    [Fact]
    public async Task Spend_is_read_back_from_routed_run_records_and_an_empty_pool_waits_until_the_next_day()
    {
        var log = new FakeLog(Routed("credit", 30, Now.AddDays(-3)));
        var routing = Routing(On(Credit), log);

        var wait = await Assert.ThrowsAsync<PoolsWaitException>(() => routing.RouteAsync("code", null, null, CancellationToken.None));

        Assert.Equal(new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero), wait.Wait.Until);
        Assert.Equal(ErrorCode.RateLimited, wait.Code);
        Assert.True(wait.Error.Retryable);
        Assert.Equal(wait.Wait.Until, await routing.WaitUntilAsync("code", null, CancellationToken.None));
    }

    [Fact]
    public async Task A_run_a_provider_refused_takes_its_pool_out_until_the_next_day()
    {
        var routing = Routing(On(Free, Credit), new FakeLog());
        var first = await routing.RouteAsync("code", null, null, CancellationToken.None);

        routing.Ended(first.Route, new ResultError(ErrorCode.RateLimited, "429", true));

        Assert.Equal("credit", (await routing.RouteAsync("code", null, null, CancellationToken.None)).Route!.Pool);
        routing.Ended(new RunRoute("credit", "cheap", "paid/small"), new ResultError(ErrorCode.SessionFailed, "x", false));
        Assert.Equal("credit", (await routing.RouteAsync("code", null, null, CancellationToken.None)).Route!.Pool);
    }

    [Fact]
    public void Only_routed_priced_runs_count_as_spend()
    {
        var (_, unrouted) = Fixtures.Run("plain", "code", "owner/repo", 5, 0);
        var free = Routed("free", 0, Now);

        var spend = PoolRouting.SpendOf([unrouted, free, Routed("credit", 2.5m, Now)]);

        Assert.Equal([new PoolSpend("credit", Now, 2.5m)], spend);
    }

    private sealed class Fixed(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeLog(params RunRecord[] runs) : IRunLog
    {
        public int Reads { get; private set; }

        public Task AppendAsync(StartRecord record, CancellationToken ct) => throw new NotSupportedException();

        public Task AppendAsync(CallRecord record, CancellationToken ct) => throw new NotSupportedException();

        public Task AppendAsync(RunRecord record, CancellationToken ct) => throw new NotSupportedException();

        public Task<RunEntry> ReadAsync(string runId, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<RunSummary>> ListAsync(RunListQuery query, CancellationToken ct) => throw new NotSupportedException();

        public Task<RunLogData> ReadAllAsync(CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(new RunLogData([], runs, [], []));
        }
    }
}
