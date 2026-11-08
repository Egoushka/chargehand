using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Economics.Api;
using Chargehand.Modules.Gateway.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;
using static Chargehand.Tests.DrivenFakes;

namespace Chargehand.Tests;

/// <summary>ADR 0095, D-113, through a driven batch: each session of a task asks the budget router, so a provider's refusal in the middle of a task moves its next session to the next pool and the task's run record
/// carries both routes; the batch's own record carries none, so no cost is counted twice.</summary>
public class PerSessionRoutingBatchTests
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";

    private static PoolSettings Pool(string name, string model) => new(name, PoolKinds.Monthly, [model], TotalUsd: 10, NoTrainingModels: ["*"]);

    private static PoolsSettings Pools(params PoolSettings[] order) => new(Enabled: true, DefaultTier: "cheap", Tiers: [new("cheap", ["anthropic/claude-a", "anthropic/claude-b"])], Order: order);

    private static readonly PoolsSettings TwoPools = Pools(Pool("first", "anthropic/claude-a"), Pool("second", "anthropic/claude-b"));

    private static SessionOutcome Refused() => new(SessionStatus.RateLimited, GatewayRefusal.BudgetReason, 12, 1000, 200, 4000, null, 0, [], false);

    private static RunRequest Request() =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([new DrivenTask("t1", Goal: "Add a retry")], MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private static string TaskRunId(ResultContract result) =>
        JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "driven-batch").Content!).RootElement.GetProperty("tasks")[0].GetProperty("run_id").GetString()!;

    [Fact]
    public async Task A_task_whose_session_a_provider_refused_goes_on_in_the_same_batch_on_the_next_pool_with_one_run_record_that_carries_both_routes()
    {
        var sessions = 0;
        using var rig = new DrivenRunTests.Rig(pools: TwoPools, outcomes: (_, _) => ++sessions == 1 ? Refused() : null);

        var result = await rig.Run.RunAsync(Request(), "run-batch-routed", Token, default);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal(["claude-a", "claude-b"], rig.Volumes.Tasks.Select(t => t.Model));       // the second session runs on the second pool's model
        var record = (await rig.Log.ReadAsync(TaskRunId(result), default)).Run!;
        Assert.Equal(["first", "second"], record.Routes!.Select(r => r.Pool));
        Assert.Equal("second", record.Route!.Pool);
        Assert.Null((await rig.Log.ReadAsync("run-batch-routed", default)).Run!.Route);          // the batch's record does not repeat what the tasks' say
        Assert.Equal(["claude-a", "claude-b"], rig.Engine.Started.Where(s => s.Env.ContainsKey("CLAUDE_CODE_SUBAGENT_MODEL")).Select(s => s.Env["CLAUDE_CODE_SUBAGENT_MODEL"]));
    }

    [Fact]
    public async Task When_every_pool_refuses_the_task_ends_rate_limited_and_retryable()
    {
        using var rig = new DrivenRunTests.Rig(pools: TwoPools, outcomes: (_, _) => Refused());

        var result = await rig.Run.RunAsync(Request(), "run-batch-refused", Token, default);

        Assert.NotEqual(ResultStatus.Completed, result.Status);
        Assert.Equal((ErrorCode.RateLimited, true), (result.Error!.Code, result.Error.Retryable));
        Assert.Equal(["claude-a", "claude-b"], rig.Volumes.Tasks.Select(t => t.Model));        // each pool was tried once, in order
    }

    [Fact]
    public async Task A_batch_with_the_router_off_runs_every_session_on_the_presets_model_and_records_no_route()
    {
        using var rig = new DrivenRunTests.Rig();

        var result = await rig.Run.RunAsync(Request(), "run-batch-unrouted", Token, default);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.DoesNotContain(rig.Engine.Started, s => s.Env.ContainsKey("CLAUDE_CODE_SUBAGENT_MODEL"));
        var record = (await rig.Log.ReadAsync(TaskRunId(result), default)).Run!;
        Assert.Null(record.Route);
        Assert.Null(record.Routes);
    }
}
