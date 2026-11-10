using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Gateway.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;
using static Chargehand.Tests.DrivenFakes;

namespace Chargehand.Modules.Tasks.Tests;

/// <summary>ADR 0095, D-113: each session of a task asks the router for its pool, so a provider's refusal in the middle of a task moves the next session to the next eligible pool
/// instead of ending the task partial.</summary>
public class PerSessionRoutingTests
{
    private static readonly RunRoute PoolA = new("pool-a", "strong", "anthropic/claude-a", "public");
    private static readonly RunRoute PoolB = new("pool-b", "strong", "anthropic/claude-b", "public");

    private static SessionOutcome Done(int commits, decimal? usd = null) => new(SessionStatus.Completed, "", 30, 1000, 200, 4000, usd, 0, [], true, Commits: commits);

    private static SessionOutcome Refused(int commits, decimal? usd = null, string reason = GatewayRefusal.BudgetReason, DateTimeOffset? resets = null) =>
        new(SessionStatus.RateLimited, reason, 12, 1000, 200, 4000, usd, 0, [], commits > 0, Commits: commits, RateLimitResetsAt: resets);

    private static SessionRoute On(RunRoute route) => new(route.Model, route);

    private sealed class FakeRouter(params SessionRoute[] answers) : ISessionRouter
    {
        public int Pools { get; init; } = 2;

        public int Asked { get; private set; }

        public List<(RunRoute Route, bool Budget)> Refusals { get; } = [];

        /// <summary>The nth answer, the last one for every ask after it.</summary>
        public Task<SessionRoute> RouteAsync(CancellationToken ct) => Task.FromResult(answers[Math.Min(Asked++, answers.Length - 1)]);

        public void Refused(RunRoute route, bool budget) => Refusals.Add((route, budget));
    }

    private static FakeVolumes Script(Func<string, SessionOutcome> outcome) => new(_ => SessionStatus.Completed, (_, goal) => outcome(goal));

    private static bool IsContinuation(string goal) => goal.Contains("Continuation", StringComparison.Ordinal);

    [Fact]
    public async Task A_refusal_in_the_middle_of_a_task_continues_it_on_the_next_pool_with_one_run_record_that_carries_both_routes()
    {
        var router = new FakeRouter(On(PoolA), On(PoolB));
        using var rig = new TaskRunnerRig(Script(goal => IsContinuation(goal) ? Done(3, usd: 2m) : Refused(2, usd: 1.5m)), router: router, priced: true);

        var outcome = await rig.RunAsync("Add a retry");

        Assert.Equal(TaskState.Completed, outcome.State);
        Assert.Equal(["claude-a", "claude-b"], rig.Volumes.Tasks.Select(t => t.Model));
        Assert.Equal((PoolA, true), Assert.Single(router.Refusals));
        Assert.Single(rig.Volumes.Seeds);                          // the second session starts from the first one's work
        var record = (await rig.Log.ReadAsync(outcome.RunId!, CancellationToken.None)).Run!;
        Assert.Equal([PoolA with { Usd = 1.5m }, PoolB with { Usd = 2m }], record.Routes);
        Assert.Equal(PoolB, record.Route);
        Assert.Equal(3.5m, record.Result.Usage.Usd);               // the routes' costs add up to the result's
    }

    [Fact]
    public async Task A_task_whose_sessions_stay_on_one_pool_records_that_route_and_no_list()
    {
        var router = new FakeRouter(On(PoolA));
        using var rig = new TaskRunnerRig(Script(_ => Done(3, usd: 1m)), router: router, priced: true);

        var outcome = await rig.RunAsync("Add a retry");

        Assert.Equal(TaskState.Completed, outcome.State);
        Assert.Equal(["claude-a"], rig.Volumes.Tasks.Select(t => t.Model));
        Assert.Empty(router.Refusals);
        var record = (await rig.Log.ReadAsync(outcome.RunId!, CancellationToken.None)).Run!;
        Assert.Equal(PoolA, record.Route);
        Assert.Null(record.Routes);
    }

    [Fact]
    public async Task A_refusal_before_any_commit_starts_the_next_session_from_the_base_on_the_next_pool()
    {
        var router = new FakeRouter(On(PoolA), On(PoolB));
        var sessions = 0;
        using var rig = new TaskRunnerRig(Script(_ => ++sessions == 1 ? Refused(0, reason: "rate_limited") : Done(2)), router: router);

        var outcome = await rig.RunAsync("Add a retry");

        Assert.Equal(TaskState.Completed, outcome.State);
        Assert.Equal(["claude-a", "claude-b"], rig.Volumes.Tasks.Select(t => t.Model));
        Assert.Equal((PoolA, false), Assert.Single(router.Refusals));   // a 429 is not the budget
        Assert.Empty(rig.Volumes.Seeds);                                 // nothing to seed
        Assert.Equal(rig.Volumes.Tasks[0].Goal, rig.Volumes.Tasks[1].Goal);   // and no work to tell the session about
    }

    [Fact]
    public async Task When_every_pool_is_refused_the_task_ends_rate_limited_until_the_router_says_a_pool_is_back()
    {
        var back = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
        var router = new FakeRouter(On(PoolA), new SessionRoute(null, null, back));
        using var rig = new TaskRunnerRig(Script(_ => Refused(2, resets: null)), router: router);

        var outcome = await rig.RunAsync("Add a retry");

        Assert.Equal((TaskState.Failed, ErrorCode.RateLimited), (outcome.State, outcome.Error));
        Assert.Equal(back, outcome.RetryAfter);
        Assert.True(outcome.Budget);
        Assert.Single(rig.Volumes.Tasks);                                // the second session never started
        Assert.Equal((PoolA, true), Assert.Single(router.Refusals));
        Assert.True(Assert.Single(rig.Handover.Inputs).Partial);         // the first session's work is kept as a partial draft
    }

    [Fact]
    public async Task When_no_pool_can_take_the_first_session_the_task_ends_rate_limited_without_starting_one()
    {
        var back = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
        var router = new FakeRouter(new SessionRoute(null, null, back));
        using var rig = new TaskRunnerRig(Script(_ => Done(3)), router: router);

        var outcome = await rig.RunAsync("Add a retry");

        Assert.Equal((TaskState.Failed, ErrorCode.RateLimited), (outcome.State, outcome.Error));
        Assert.Equal(back, outcome.RetryAfter);
        Assert.True(outcome.Budget);
        Assert.Empty(rig.Volumes.Tasks);
        Assert.Empty(rig.Engine.Started);
    }

    [Fact]
    public async Task A_task_moves_to_another_pool_at_most_as_many_times_as_the_router_has_pools()
    {
        var router = new FakeRouter(On(PoolA), On(PoolB), On(PoolA)) { Pools = 2 };
        using var rig = new TaskRunnerRig(Script(_ => Refused(2)), router: router);

        var outcome = await rig.RunAsync("Add a retry");

        Assert.Equal((TaskState.Failed, ErrorCode.RateLimited), (outcome.State, outcome.Error));
        Assert.Equal(3, rig.Volumes.Tasks.Count);                        // the first session and two moves
        Assert.Equal(3, router.Refusals.Count);
    }

    [Fact]
    public async Task A_router_that_blocks_the_task_fails_it_as_the_router_does_and_starts_nothing()
    {
        var router = new ThrowingRouter();
        using var rig = new TaskRunnerRig(Script(_ => Done(3)), router: router);

        var failure = await Assert.ThrowsAsync<ChargehandException>(() => rig.RunAsync("Add a retry"));

        Assert.Equal(ErrorCode.RepositoryNotAllowed, failure.Code);
        Assert.Empty(rig.Engine.Started);
    }

    private sealed class ThrowingRouter : ISessionRouter
    {
        public int Pools => 1;

        public Task<SessionRoute> RouteAsync(CancellationToken ct) => throw new ChargehandException(ErrorCode.RepositoryNotAllowed, "no pool may see this repository's data", "Add a pool with no_training_models.");

        public void Refused(RunRoute route, bool budget)
        {
        }
    }
}
