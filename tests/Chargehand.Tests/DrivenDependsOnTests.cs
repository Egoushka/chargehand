using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0049: tasks that depend on others start from their branches, wait for them, and survive a restart in the middle of a chain. The git work is faked here;
/// <see cref="GitStackSeederTests"/> runs it for real.</summary>
public class DrivenDependsOnTests
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";
    private const string BatchId = "run-batch-dep";

    internal sealed class FakeStacker : IStackSeeder
    {
        public List<StackRequest> Requests { get; } = [];

        public Func<StackRequest, StackResult> Script { get; set; } = _ => StackResult.Ok(2);

        public Task<StackResult> BuildAsync(StackRequest request, CancellationToken ct)
        {
            lock (Requests)
                Requests.Add(request);
            var made = Script(request);
            if (made.Made)
                File.WriteAllText(request.OutBundle, "stack");
            return Task.FromResult(made);
        }
    }

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private static DrivenTask Item(string id, params string[] dependsOn) => new(id, Goal: $"goal of {id}", DependsOn: dependsOn.Length == 0 ? null : dependsOn);

    private static RunRequest Request(int parallel, params DrivenTask[] tasks) =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven(tasks, MaxParallel: parallel, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    private static List<JsonElement> Rows(ResultContract result) =>
        [.. JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "driven-batch").Content!).RootElement.GetProperty("tasks").EnumerateArray().Select(e => e.Clone())];

    private static string Status(JsonElement row) => row.GetProperty("status").GetString()!;

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 400; i++)
        {
            if (condition())
                return;
            await Task.Delay(25);
        }
        Assert.Fail("the condition did not hold within 10 seconds");
    }

    [Fact]
    public async Task A_request_whose_dependencies_name_an_unknown_task_or_form_a_cycle_is_refused_before_anything_starts()
    {
        using var rig = new DrivenRunTests.Rig(stacker: new FakeStacker());
        var unknown = await rig.Run.RunAsync(Request(2, Item("a", "zzz")), "run-batch-u", Token, default);
        var cycle = await rig.Run.RunAsync(Request(2, Item("a", "b"), Item("b", "c"), Item("c", "a")), "run-batch-c", Token, default);

        Assert.Equal(ErrorCode.InvalidRequest, unknown.Error!.Code);
        Assert.Contains("task 'a': depends_on names 'zzz', which is not a task of this batch", unknown.Error.Message);
        Assert.Equal(ErrorCode.InvalidRequest, cycle.Error!.Code);
        Assert.Contains("cycle: a -> b -> c -> a", cycle.Error.Message);
        Assert.Empty(rig.Engine.Started);
    }

    [Fact]
    public async Task A_task_stacked_on_another_starts_from_its_branch_and_its_pull_request_is_based_on_it()
    {
        var stacker = new FakeStacker();
        using var rig = new DrivenRunTests.Rig(stacker: stacker);
        List<RunStatus> events = [];
        var result = await rig.Run.RunAsync(Request(2, Item("a"), Item("b", "a")), BatchId, Token, default, e => { lock (events) events.Add(e); });

        Assert.Equal(ResultStatus.Completed, result.Status);
        var (a, b) = (rig.Handover.Inputs[0], rig.Handover.Inputs[1]);
        Assert.Equal("goal of a", rig.Volumes.Tasks[0].Goal);
        Assert.Equal(("main", null), (a.BaseBranch, a.FallbackBaseBranch));

        // b's container started only after a's pull request opened, from a seed made of a's branch under b's own branch name.
        Assert.True(events.FindIndex(e => e is { TaskId: "a", Event: RunEventKind.TaskFinished }) < events.FindIndex(e => e is { TaskId: "b", Event: RunEventKind.ContainerStarted }));
        var request = Assert.Single(stacker.Requests);
        Assert.Equal(b.Branch, request.Branch);
        Assert.Equal([new StackInput("a", a.Branch, Path.Combine(request.ScratchRoot, "out", a.RunId, "chargehand.bundle"))], request.Inputs);
        Assert.Equal((Commit, "/srv/checkouts/repo-abc1234"), (request.BaseCommit, request.SourcePath));
        Assert.Equal((rig.Engine.Started[1].RunId, request.OutBundle), Assert.Single(rig.Volumes.Seeds));
        Assert.Contains("is already on your branch", rig.Volumes.Tasks[1].Goal);
        Assert.StartsWith("goal of b", rig.Volumes.Tasks[1].Goal);

        Assert.Equal((a.Branch, "main"), (b.BaseBranch, b.FallbackBaseBranch));
        Assert.Contains($"Stacked on `{a.Branch}` (task 'a'", b.Body);
        Assert.Null(b.PolicyNote);
        Assert.Contains(events, e => e is { TaskId: "b", Event: RunEventKind.ContainerStarted, Detail: "task b: session container started (stacked on task 'a')" });
    }

    [Fact]
    public async Task A_task_that_depends_on_several_starts_from_a_merge_of_their_branches_and_is_based_on_the_default_branch()
    {
        var stacker = new FakeStacker();
        using var rig = new DrivenRunTests.Rig(stacker: stacker);
        List<RunStatus> events = [];
        var result = await rig.Run.RunAsync(Request(2, Item("a"), Item("b"), Item("c", "b", "a")), BatchId, Token, default, e => { lock (events) events.Add(e); });

        Assert.Equal(ResultStatus.Completed, result.Status);
        var request = Assert.Single(stacker.Requests);
        Assert.Equal(["b", "a"], request.Inputs.Select(i => i.TaskId));
        var c = rig.Handover.Inputs[2];
        Assert.Equal(("main", null), (c.BaseBranch, c.FallbackBaseBranch));
        Assert.Contains("Depends on tasks 'b', 'a': the branch starts from a merge of their branches", c.Body);
        Assert.Contains(events, e => e is { TaskId: "c", Event: RunEventKind.ContainerStarted, Detail: "task c: session container started (starts from the merged branches of tasks 'b', 'a')" });
    }

    [Fact]
    public async Task A_merge_conflict_fails_the_task_with_the_conflicting_paths_and_starts_no_container_for_it()
    {
        var stacker = new FakeStacker { Script = _ => new StackResult(false, ["src/Retry.cs", "docs/guide.md"], "b", null, 0) };
        using var rig = new DrivenRunTests.Rig(stacker: stacker);
        List<RunStatus> events = [];
        var result = await rig.Run.RunAsync(Request(2, Item("a"), Item("b"), Item("c", "a", "b")), BatchId, Token, default, e => { lock (events) events.Add(e); });

        Assert.Equal(ErrorCode.TasksIncomplete, result.Error!.Code);
        var c = Rows(result)[2];
        Assert.Equal(("failed", "merge_conflict"), (Status(c), c.GetProperty("error_code").GetString()));
        Assert.Contains("task 'b' conflicts in src/Retry.cs, docs/guide.md", c.GetProperty("detail").GetString());
        Assert.Equal(2, rig.Engine.Started.Count);
        Assert.Equal(2, rig.Handover.Inputs.Count);
        Assert.Contains(events, e => e is { TaskId: "c", Event: RunEventKind.TaskFinished } && e.Detail!.Contains("src/Retry.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_dependency_that_failed_leaves_the_dependent_not_started_with_the_reason()
    {
        using var rig = new DrivenRunTests.Rig(script: goal => goal.Contains("goal of a", StringComparison.Ordinal) ? SessionStatus.Failed : SessionStatus.Completed, stacker: new FakeStacker());
        List<RunStatus> events = [];
        var result = await rig.Run.RunAsync(Request(2, Item("a"), Item("b", "a"), Item("c", "b"), Item("d")), BatchId, Token, default, e => { lock (events) events.Add(e); });

        var rows = Rows(result);
        Assert.Equal(["failed", "not_started", "not_started", "completed"], rows.Select(Status));
        Assert.Equal("not started: the task it depends on, 'a', failed", rows[1].GetProperty("detail").GetString());
        Assert.Equal("not started: the task it depends on, 'b', was not started", rows[2].GetProperty("detail").GetString());
        Assert.Equal(2, rig.Engine.Started.Count);                                       // a and d only
        Assert.Contains(events, e => e is { TaskId: "b", Event: RunEventKind.TaskFinished } && e.Detail!.StartsWith("task b: not_started", StringComparison.Ordinal));
        Assert.Equal(ErrorCode.TasksIncomplete, result.Error!.Code);
    }

    [Fact]
    public async Task A_restart_in_the_middle_of_a_chain_keeps_the_finished_link_reattaches_the_running_one_and_starts_the_last_from_it()
    {
        var stacker = new FakeStacker();
        using var rig = new DrivenRunTests.Rig(durable: true, stacker: stacker);
        var request = Request(1, Item("a"), Item("b", "a"), Item("c", "b"));
        rig.Engine.HoldNth = 2;
        using var stop = new CancellationTokenSource();
        var first = rig.Run.RunAsync(request, BatchId, Token, stop.Token, null, () => false);
        await Until(() => rig.Engine.Started.Count == 2 && rig.Handover.Inputs.Count == 1);
        await stop.CancelAsync();
        await first;
        var state = Assert.Single(await rig.Restart().UnfinishedAsync(default));

        Assert.Equal([TaskPhase.HandedOver, TaskPhase.Started, TaskPhase.Pending], state.Tasks.Select(t => t.Phase));
        Assert.Equal([null, "a", "b"], state.Tasks.Select(t => t.DependsOn is null ? null : string.Join(',', t.DependsOn)));
        Assert.Single(stacker.Requests);                                                  // b was stacked on a before the server stopped

        rig.Engine.Release();
        var result = await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal(["completed", "completed", "completed"], Rows(result).Select(Status));
        Assert.Equal(3, rig.Engine.Started.Count);                                         // b's container was taken up, not started again
        Assert.Equal(2, stacker.Requests.Count);
        var (b, c) = (rig.Handover.Inputs[1], rig.Handover.Inputs[2]);
        Assert.Equal($"chargehand/{state.Tasks[1].RunId}", b.Branch);
        Assert.Equal([new StackInput("b", b.Branch, Path.Combine(stacker.Requests[1].ScratchRoot, "out", state.Tasks[1].RunId!, "chargehand.bundle"))], stacker.Requests[1].Inputs);
        Assert.Equal(b.Branch, c.BaseBranch);
    }
}
