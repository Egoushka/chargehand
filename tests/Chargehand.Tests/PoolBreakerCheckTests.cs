using Chargehand.Modules.Events.Api;
using Chargehand.Modules.Pool.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

public sealed class PoolBreakerCheckTests
{
    private static readonly PoolScope Scope = new("owner/app", "area");

    private sealed class Log : IEventLog
    {
        public List<NewEvent> All { get; } = [];

        public EventSignal Signal { get; } = new();

        public Task<IReadOnlyList<long>> AppendAsync(IReadOnlyList<NewEvent> batch, CancellationToken ct)
        {
            All.AddRange(batch);
            return Task.FromResult<IReadOnlyList<long>>([.. batch.Select((_, i) => (long)i)]);
        }

        public Task<IReadOnlyList<StoredEvent>> ReadAsync(EventFilter filter, CancellationToken ct) => Task.FromResult<IReadOnlyList<StoredEvent>>([]);
    }

    [Fact]
    public async Task Refused_checkout_is_one_problem_and_no_breaker_count()
    {
        var log = new Log();
        var time = new ManualTimeProvider();
        var breaker = new PoolBreakerCheck(log, time);
        const string refusal = "the checkout has a denied file: .env";

        for (var i = 0; i < 5; i++)
            Assert.True(await breaker.RefusedAsync(Scope.Repository, refusal));

        Assert.Single(log.All, e => e.Type == "chargehand.sweep.problem");
        Assert.Null(await breaker.HoldAsync(Scope, CancellationToken.None));
        Assert.Contains("denied file", await breaker.Problems.HoldAsync(Scope, CancellationToken.None));
        Assert.Equal("problem", breaker.Problems.Name);
        time.Advance(PoolBreakerCheck.ProblemFor + TimeSpan.FromMinutes(1));
        Assert.Null(await breaker.Problems.HoldAsync(Scope, CancellationToken.None));
    }

    [Fact]
    public async Task Three_failures_in_a_row_hold_the_repository_and_a_success_resets_the_count()
    {
        var time = new ManualTimeProvider();
        var breaker = new PoolBreakerCheck(null, time);

        breaker.Finished(Scope.Repository, true);
        breaker.Finished(Scope.Repository, true);
        breaker.Finished(Scope.Repository, false);
        breaker.Finished(Scope.Repository, true);
        breaker.Finished(Scope.Repository, true);
        Assert.Null(await breaker.HoldAsync(Scope, CancellationToken.None));

        breaker.Finished(Scope.Repository, true);
        Assert.NotNull(await breaker.HoldAsync(Scope, CancellationToken.None));
        Assert.Null(await breaker.HoldAsync(new PoolScope("owner/other", "area"), CancellationToken.None));
        time.Advance(PoolBreakerCheck.BreakerFor + TimeSpan.FromMinutes(1));
        Assert.Null(await breaker.HoldAsync(Scope, CancellationToken.None));
    }

    [Fact]
    public async Task A_refusal_that_is_capacity_is_not_a_repository_problem()
    {
        var breaker = new PoolBreakerCheck(new Log(), new ManualTimeProvider());

        Assert.False(await breaker.RefusedAsync(Scope.Repository, "the daily cap is reached"));
        Assert.Null(await breaker.Problems.HoldAsync(Scope, CancellationToken.None));
        Assert.Empty(breaker.Active());
    }
}
