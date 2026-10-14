using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0049: the planning step in a batch, tested with a fake planner. What a plan may change and the real planner's prompt and parsing are tested in the Batches module's
/// <c>BatchPlannerTests</c>.</summary>
public class DrivenPlanningTests
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";
    private const string BatchId = "run-batch-plan";

    private static ProposedTask Plan(string id, string? size = null, ProposedPart[]? split = null, ProposedNumber[]? assign = null) => new(id, size, split, assign);

    private static PlanProposal Proposal(params ProposedTask[] tasks) => new(tasks, ["listed db/migrations: latest is 0041"], null, "run-plan-1");

    private static ProposedPart Part(string goal, string? size = "M") => new(goal, size);

    private static ProposedNumber Number(string what, string value, int? part = null) => new(what, value, part);

    private sealed class FakePlanner(PlanProposal? proposal = null, Exception? fail = null) : IBatchPlanner
    {
        public int Calls;
        public PlanInput? Last;

        public Task<PlanProposal> ProposeAsync(PlanInput input, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Last = input;
            return fail is null ? Task.FromResult(proposal!) : Task.FromException<PlanProposal>(fail);
        }
    }

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private static RunRequest Request(DrivenTask[] tasks, bool? plan = null, int parallel = 2) =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven(tasks, MaxParallel: parallel, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m, Plan: plan));

    private static DrivenTask Goal(string id, string? size = null, params string[] dependsOn) => new(id, Goal: $"goal of {id}", Size: size, DependsOn: dependsOn.Length == 0 ? null : dependsOn);

    private static List<JsonElement> Rows(ResultContract result) =>
        [.. JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "driven-batch").Content!).RootElement.GetProperty("tasks").EnumerateArray().Select(e => e.Clone())];

    private static JsonElement PlanArtifact(ResultContract result) => JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "plan").Content!).RootElement;

    [Fact]
    public async Task A_planned_batch_runs_sized_split_and_numbered_and_the_result_carries_the_plan()
    {
        var planner = new FakePlanner(Proposal(
            Plan("big", "L", split: [Part("first half of big", "S"), Part("second half of big", "M")]),
            Plan("m1", "S", assign: [Number("migration number", "0042")]),
            Plan("m2", "M", assign: [Number("migration number", "0043")])));
        var stacker = new DrivenDependsOnTests.FakeStacker();
        using var rig = new DrivenRunTests.Rig(planner: planner, stacker: stacker);
        List<RunStatus> events = [];
        var result = await rig.Run.RunAsync(Request([Goal("big"), Goal("m1"), Goal("m2")], plan: true), BatchId, Token, default, e => { lock (events) events.Add(e); });

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal(["big-1", "big", "m1", "m2"], Rows(result).Select(r => r.GetProperty("id").GetString()));
        Assert.All(Rows(result), r => Assert.Equal("completed", r.GetProperty("status").GetString()));

        // The chain ran in order: the second link started from the first's branch.
        var request = Assert.Single(stacker.Requests);
        Assert.Equal("big-1", Assert.Single(request.Inputs).TaskId);
        var goals = rig.Volumes.Tasks.Select(t => t.Goal).ToList();
        Assert.Contains(goals, g => g.StartsWith("first half of big", StringComparison.Ordinal));
        Assert.Contains(goals, g => g.StartsWith("second half of big", StringComparison.Ordinal) && g.Contains("is already on your branch", StringComparison.Ordinal));
        Assert.Contains(goals, g => g.Contains("use migration number 0042", StringComparison.Ordinal));
        Assert.Contains(goals, g => g.Contains("use migration number 0043", StringComparison.Ordinal));
        string SizeOf(string prefix) => rig.Volumes.Tasks.Single(t => t.Goal.StartsWith(prefix, StringComparison.Ordinal)).Governor!.SizeClass!;
        Assert.Equal(("S", "M", "S", "M"), (SizeOf("first half"), SizeOf("second half"), SizeOf("goal of m1"), SizeOf("goal of m2")));

        var plan = PlanArtifact(result);
        Assert.Equal(("applied", "request"), (plan.GetProperty("status").GetString(), plan.GetProperty("source").GetString()));
        Assert.Equal(["big-1", "big", "m1", "m2"], plan.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("id").GetString()));
        Assert.Equal("listed db/migrations: latest is 0041", plan.GetProperty("checked")[0].GetString());
        var published = Assert.Single(events, e => e.Event == RunEventKind.Plan);
        Assert.StartsWith("plan: big-1 S (split from big); big M (split from big); m1 S [migration number 0042]; m2 M [migration number 0043]. Checked: listed db/migrations", published.Detail);
        Assert.Equal(1, planner.Calls);
        Assert.Equal(["big", "m1", "m2"], planner.Last!.Tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task The_caller_overrides_the_plan_a_given_size_depends_on_and_budget_stand()
    {
        var planner = new FakePlanner(Proposal(
            Plan("held", "L", split: [Part("one"), Part("two")]),
            Plan("after", "L", split: [Part("one"), Part("two")]),
            Plan("sized", "L")));
        using var rig = new DrivenRunTests.Rig(planner: planner, stacker: new DrivenDependsOnTests.FakeStacker());
        DrivenTask[] tasks = [Goal("held", "S"), Goal("after", null, "held"), Goal("sized", "S")];
        var result = await rig.Run.RunAsync(Request(tasks, plan: true), BatchId, Token, default);

        var rows = Rows(result);
        // "held" and "sized" keep their size S and stay whole; "after" was unsized, so the plan's chain applies, and its first link still waits for "held".
        Assert.Equal(["held", "after-1", "after", "sized"], rows.Select(r => r.GetProperty("id").GetString()));
        Assert.Equal("completed", rows[0].GetProperty("status").GetString());
        var plan = PlanArtifact(result);
        var entries = plan.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal(("S", "caller"), (entries[0].GetProperty("size").GetString(), entries[0].GetProperty("size_from").GetString()));
        Assert.Equal(["held"], entries[1].GetProperty("depends_on").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(["after-1"], entries[2].GetProperty("depends_on").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(("S", "caller"), (entries[3].GetProperty("size").GetString(), entries[3].GetProperty("size_from").GetString()));
        Assert.Equal(["S", "S"], rig.Volumes.Tasks.Where(t => t.Goal.StartsWith("goal of held", StringComparison.Ordinal) || t.Goal.StartsWith("goal of sized", StringComparison.Ordinal)).Select(t => t.Governor!.SizeClass));
    }

    [Fact]
    public async Task The_profile_turns_planning_on_and_the_request_turns_it_off()
    {
        var planner = new FakePlanner(Proposal(Plan("a", "S")));
        using var rig = new DrivenRunTests.Rig(planner: planner, planning: new DrivenPlanning(Enabled: true));
        var onByProfile = await rig.Run.RunAsync(Request([Goal("a")]), "run-batch-p1", Token, default);
        Assert.Equal("profile", PlanArtifact(onByProfile).GetProperty("source").GetString());
        Assert.Equal(1, planner.Calls);

        var offByRequest = await rig.Run.RunAsync(Request([Goal("a")], plan: false), "run-batch-p2", Token, default);
        Assert.DoesNotContain(offByRequest.Artifacts, a => a.Kind == "plan");
        Assert.Equal(1, planner.Calls);

        using var off = new DrivenRunTests.Rig(planner: planner);
        var untouched = await off.Run.RunAsync(Request([Goal("a")]), "run-batch-p3", Token, default);
        Assert.DoesNotContain(untouched.Artifacts, a => a.Kind == "plan");
        Assert.Equal(1, planner.Calls);
    }

    [Theory]
    [InlineData("fails")]
    [InlineData("missing")]
    public async Task A_planner_that_fails_or_is_missing_leaves_the_batch_as_sent_and_the_plan_says_why(string how)
    {
        var planner = how == "fails" ? new FakePlanner(fail: new PlanException("the planning run printed no JSON plan")) : null;
        using var rig = new DrivenRunTests.Rig(planner: planner);
        List<RunStatus> events = [];
        var result = await rig.Run.RunAsync(Request([Goal("a"), Goal("b", "S")], plan: true), BatchId, Token, default, e => { lock (events) events.Add(e); });

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal(["a", "b"], Rows(result).Select(r => r.GetProperty("id").GetString()));
        var plan = PlanArtifact(result);
        Assert.Equal("skipped", plan.GetProperty("status").GetString());
        Assert.Equal(how == "fails" ? "the planning run printed no JSON plan" : "this server has no planner", plan.GetProperty("reason").GetString());
        Assert.StartsWith("plan skipped: ", Assert.Single(events, e => e.Event == RunEventKind.Plan).Detail);
        Assert.Equal(["goal of a", "goal of b"], rig.Volumes.Tasks.Select(t => t.Goal).Order());
    }

    [Fact]
    public async Task A_restart_carries_the_planned_batch_and_does_not_plan_again()
    {
        var planner = new FakePlanner(Proposal(Plan("big", "L", split: [Part("first half"), Part("second half")])));
        using var rig = new DrivenRunTests.Rig(durable: true, planner: planner, stacker: new DrivenDependsOnTests.FakeStacker());
        var request = Request([Goal("big"), Goal("other")], plan: true, parallel: 1);
        rig.Engine.HoldNth = 1;
        using var stop = new CancellationTokenSource();
        var first = rig.Run.RunAsync(request, BatchId, Token, stop.Token, null, () => false);
        for (var i = 0; i < 400 && rig.Engine.Started.Count < 1; i++)
            await Task.Delay(25);
        await stop.CancelAsync();
        await first;
        var state = Assert.Single(await rig.Restart().UnfinishedAsync(default));

        Assert.Equal(["big-1", "big", "other"], state.Tasks.Select(t => t.Id));
        Assert.Equal([null, "big-1", null], state.Tasks.Select(t => t.DependsOn is null ? null : string.Join(',', t.DependsOn)));
        Assert.Equal(["big", "big", null], state.Tasks.Select(t => t.SplitFrom));
        Assert.True(state.Plan!.Applied);

        rig.Engine.Release();
        var result = await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal(["big-1", "big", "other"], Rows(result).Select(r => r.GetProperty("id").GetString()));
        Assert.Equal(1, planner.Calls);
        Assert.Equal("applied", PlanArtifact(result).GetProperty("status").GetString());
    }
}
