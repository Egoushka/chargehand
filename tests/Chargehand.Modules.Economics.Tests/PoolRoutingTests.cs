using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Economics.Api;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Modules.Economics.Tests;

/// <summary>The router as the call sites use it (ADR 0095): off changes nothing, on names the model and who pays, reads spend back from routed run records but only warns about it, takes a pool out when its
/// provider refuses (a 402 until the refill, a 429 until tomorrow) so the next pool takes the call, and keeps restricted data off pools that may train.</summary>
public class PoolRoutingTests
{
    private static readonly PoolSettings Free = new("free", PoolKinds.Free, ["free/small"]);
    private static readonly PoolSettings Credit = new("credit", PoolKinds.Monthly, ["paid/small", "paid/big"], TotalUsd: 30, NoTrainingModels: ["*"]);

    private static PoolsSettings On(params PoolSettings[] order) => new(Enabled: true, DefaultTier: "cheap",
        Tiers: [new("cheap", ["free/small", "paid/small"]), new("strong", ["paid/big"])],
        Routes: new Dictionary<string, string> { ["driven/L"] = "strong", ["review"] = "strong" }, Order: order);

    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static PoolRouting Routing(PoolsSettings? settings, FakeLog log, IDataClassifier? classifier = null, Clock? clock = null) => new(settings, log, clock ?? new Clock(Now), classifier);

    private static Task<Routed> Route(PoolRouting routing, DataClass dataClass = DataClass.Public, string kind = "code", string? size = null) =>
        routing.RouteAsync(kind, size, null, dataClass, CancellationToken.None);

    private static RunRecord Routed(string pool, decimal usd, DateTimeOffset finished, ErrorCode? error = null)
    {
        var (_, run) = Fixtures.Run($"run-{pool}-{usd}", "driven", "owner/repo", usd, 0);
        return run with { Finished = finished, Route = new RunRoute(pool, "cheap", "paid/small"), Result = error is { } code ? run.Result with { Error = new ResultError(code, "limited", true) } : run.Result };
    }

    [Fact]
    public async Task Off_keeps_the_callers_model_and_reads_and_classifies_nothing()
    {
        var log = new FakeLog();
        var classifier = new FakeClassifier(DataClass.Public);
        var routing = Routing(On(Free) with { Enabled = false }, log, classifier);

        var routed = await routing.RouteAsync("driven", "L", "anthropic/claude-sonnet-5", DataClass.Restricted, CancellationToken.None);

        Assert.Equal(new Routed("anthropic/claude-sonnet-5", null), routed);
        Assert.False(routing.On);
        Assert.Equal(0, log.Reads);
        Assert.Equal(DataClass.Restricted, await routing.ClassifyAsync("/repo", CancellationToken.None));
        Assert.Empty(classifier.Asked);
        Assert.Null(await Routing(null, log).WaitUntilAsync("driven", null, DataClass.Public, CancellationToken.None));
        Assert.False(routing.CanRoute("driven", "L", DataClass.Public));
        Assert.False(routing.Ended(new RunRoute("free", "cheap", "free/small"), new ResultError(ErrorCode.RateLimited, "429", true)));
    }

    [Fact]
    public async Task On_names_the_routers_model_the_pool_that_pays_and_the_class_of_the_data_whatever_the_caller_resolved()
    {
        var routing = Routing(On(Free, Credit), new FakeLog());

        Assert.Equal(new Routed("free/small", new RunRoute("free", "cheap", "free/small", "public")), await Route(routing));
        Assert.Equal(new Routed("paid/big", new RunRoute("credit", "strong", "paid/big", "restricted")), await Route(routing, DataClass.Restricted, "driven", "L"));
        Assert.Equal("owner_private", (await Route(routing, DataClass.OwnerPrivate)).Route!.DataClass);
    }

    [Fact]
    public async Task A_pool_over_its_limit_still_takes_the_call_and_the_call_carries_the_warning()
    {
        var routing = Routing(On(Free, Credit), new FakeLog(Routed("credit", 30, Now.AddDays(-3))));

        var routed = await Route(routing, DataClass.Restricted);

        Assert.Equal("credit", routed.Route!.Pool);
        Assert.Equal("pool 'credit': empty", routed.Warning);
        Assert.Null((await Route(routing)).Warning);
    }

    [Fact]
    public async Task A_limit_is_told_once_per_routed_call_and_never_for_a_probe()
    {
        List<string> told = [];
        var routing = new PoolRouting(On(Free, Credit), new FakeLog(Routed("credit", 30, Now.AddDays(-3))), new Clock(Now), warn: told.Add);

        await Route(routing, DataClass.Restricted);
        await routing.WaitUntilAsync("code", null, DataClass.Restricted, CancellationToken.None);
        await Route(routing);

        Assert.Equal(["pool 'credit': empty"], told);
    }

    [Fact]
    public async Task A_run_the_provider_rate_limited_takes_its_pool_out_until_the_next_day()
    {
        var routing = Routing(On(Free, Credit), new FakeLog());
        var first = await Route(routing);

        Assert.True(routing.Ended(first.Route, new ResultError(ErrorCode.RateLimited, "429", true)));

        Assert.Equal("credit", (await Route(routing)).Route!.Pool);
        Assert.False(routing.Ended(new RunRoute("credit", "cheap", "paid/small"), new ResultError(ErrorCode.SessionFailed, "x", false)));
        Assert.Equal("credit", (await Route(routing)).Route!.Pool);
    }

    [Fact]
    public async Task A_rate_limited_pool_is_back_the_next_day()
    {
        var clock = new Clock(Now);
        var routing = Routing(On(Free, Credit), new FakeLog(), clock: clock);
        routing.Refused(new RunRoute("free", "cheap", "free/small"), budget: false);

        clock.Now = new DateTimeOffset(2026, 11, 10, 23, 59, 0, TimeSpan.Zero);
        Assert.Equal("credit", (await Route(routing)).Route!.Pool);
        clock.Now = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("free", (await Route(routing)).Route!.Pool);
    }

    [Fact]
    public async Task A_spent_budget_takes_its_pool_out_until_its_refill_and_the_next_pool_takes_the_call()
    {
        var routing = Routing(On(Credit, Free), new FakeLog());

        Assert.True(routing.Ended(new RunRoute("credit", "cheap", "paid/small"), new ResultError(ErrorCode.SessionFailed, "402 Payment Required", false)));

        Assert.Equal("free", (await Route(routing)).Route!.Pool);
    }

    [Fact]
    public async Task A_pool_whose_budget_is_spent_stays_out_until_its_refill()
    {
        var clock = new Clock(Now);
        var routing = Routing(On(Credit), new FakeLog(), clock: clock);
        routing.Refused(new RunRoute("credit", "cheap", "paid/small"), budget: true);

        await Assert.ThrowsAsync<PoolsWaitException>(() => Route(routing, DataClass.Restricted));
        clock.Now = new DateTimeOffset(2026, 11, 30, 23, 59, 0, TimeSpan.Zero);
        await Assert.ThrowsAsync<PoolsWaitException>(() => Route(routing, DataClass.Restricted));
        clock.Now = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("credit", (await Route(routing, DataClass.Restricted)).Route!.Pool);
    }

    [Fact]
    public async Task Every_pool_refused_is_a_retryable_rate_limit_that_names_when_one_may_take_the_call_again()
    {
        var routing = Routing(On(Free, Credit), new FakeLog());
        routing.Refused(new RunRoute("free", "cheap", "free/small"), budget: false);
        routing.Refused(new RunRoute("credit", "cheap", "paid/small"), budget: false);

        var wait = await Assert.ThrowsAsync<PoolsWaitException>(() => Route(routing));

        Assert.Equal(new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero), wait.Wait.Until);
        Assert.Equal(ErrorCode.RateLimited, wait.Code);
        Assert.True(wait.Error.Retryable);
        Assert.Equal(wait.Wait.Until, await routing.WaitUntilAsync("code", null, DataClass.Public, CancellationToken.None));
        Assert.False(routing.CanRoute("code", null, DataClass.Public));
    }

    [Fact]
    public async Task Restricted_data_with_no_pool_that_keeps_prompts_private_fails_with_the_reason_and_is_never_waited_on()
    {
        var routing = Routing(On(Free), new FakeLog());

        var blocked = await Assert.ThrowsAsync<ChargehandException>(() => Route(routing, DataClass.Restricted));

        Assert.Equal(ErrorCode.RepositoryNotAllowed, blocked.Code);
        Assert.Contains("no_training_models", blocked.Action);
        Assert.Null(await routing.WaitUntilAsync("code", null, DataClass.Restricted, CancellationToken.None));
        Assert.False(routing.CanRoute("code", null, DataClass.Restricted));
        Assert.True(routing.CanRoute("code", null, DataClass.Public));
    }

    [Fact]
    public async Task Restricted_data_skips_a_pool_that_may_train_where_public_data_drains_it_first()
    {
        var routing = Routing(On(Free, Credit), new FakeLog());

        Assert.Equal("free", (await Route(routing, DataClass.Public)).Route!.Pool);
        Assert.Equal("credit", (await Route(routing, DataClass.Restricted)).Route!.Pool);
    }

    [Fact]
    public async Task The_class_comes_from_the_classifier_and_a_run_without_one_is_restricted()
    {
        var classifier = new FakeClassifier(DataClass.OwnerPrivate);

        Assert.Equal(DataClass.OwnerPrivate, await Routing(On(Free, Credit), new FakeLog(), classifier).ClassifyAsync("/repo", CancellationToken.None));
        Assert.Equal(["/repo"], classifier.Asked);
        Assert.Equal(DataClass.Restricted, await Routing(On(Free, Credit), new FakeLog()).ClassifyAsync("/repo", CancellationToken.None));
    }

    [Fact]
    public void The_pools_to_fall_through_are_the_ones_in_the_order()
    {
        Assert.Equal(2, Routing(On(Free, Credit), new FakeLog()).Pools);
        Assert.Equal(0, Routing(null, new FakeLog()).Pools);
    }

    [Fact]
    public void Only_routed_priced_runs_count_as_spend()
    {
        var (_, unrouted) = Fixtures.Run("plain", "code", "owner/repo", 5, 0);
        var free = Routed("free", 0, Now);

        var spend = PoolRouting.SpendOf([unrouted, free, Routed("credit", 2.5m, Now)]);

        Assert.Equal([new PoolSpend("credit", Now, 2.5m)], spend);
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeClassifier(DataClass answer) : IDataClassifier
    {
        public List<string?> Asked { get; } = [];

        public Task<DataClass> ClassifyAsync(string? repositoryPath, CancellationToken ct)
        {
            Asked.Add(repositoryPath);
            return Task.FromResult(answer);
        }
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
