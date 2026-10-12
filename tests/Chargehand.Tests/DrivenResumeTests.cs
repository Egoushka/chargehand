using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>ADR 0043: a batch carries on after the server restarts. "Restart" is a second <see cref="DrivenRun"/> on the same run log, state directory and (fake) container engine.</summary>
public class DrivenResumeTests
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";
    private const string BatchId = "run-batch-r";

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private static RunRequest Request(params string[] goals) =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([.. goals.Select((g, i) => new DrivenTask($"t{i + 1}", Goal: g))], MaxParallel: 1, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

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

    /// <summary>Three tasks one at a time: t1 handed over, t2's container running, t3 not started; then the server stops.</summary>
    private static async Task<BatchState> StopMidBatch(DrivenRunTests.Rig rig)
    {
        rig.Engine.HoldNth = 2;
        using var stop = new CancellationTokenSource();
        var first = rig.Run.RunAsync(Request("one", "two", "three"), BatchId, Token, stop.Token, null, () => false);
        await Until(() => rig.Engine.Started.Count == 2 && rig.Handover.Inputs.Count == 1);
        await stop.CancelAsync();
        await first;

        Assert.Null((await rig.Log.ReadAsync(BatchId, default)).Run);
        Assert.DoesNotContain(rig.Engine.Calls, c => c.StartsWith("network-rm", StringComparison.Ordinal));
        Assert.DoesNotContain($"rm {"cid-" + rig.Engine.Started[1].RunId}", rig.Engine.Calls);
        Assert.Single(Directory.GetFiles(rig.StateDirectory, "*.json"));
        return Assert.Single(await rig.Restart().UnfinishedAsync(default));
    }

    private static JsonElementTasks Tasks(ResultContract result) => new(result);

    private sealed class JsonElementTasks(ResultContract result)
    {
        public List<(string Id, string Status)> Rows { get; } = [.. System.Text.Json.JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "driven-batch").Content!).RootElement
            .GetProperty("tasks").EnumerateArray().Select(t => (t.GetProperty("id").GetString()!, t.GetProperty("status").GetString()!))];
    }

    [Fact]
    public async Task A_restarted_batch_keeps_finished_tasks_reattaches_the_running_one_and_runs_the_rest()
    {
        using var rig = new DrivenRunTests.Rig(durable: true);
        var state = await StopMidBatch(rig);
        var firstRun = state.Tasks[0].RunId;
        var secondRun = state.Tasks[1].RunId;
        Assert.Equal([TaskPhase.HandedOver, TaskPhase.Started, TaskPhase.Pending], state.Tasks.Select(t => t.Phase));

        rig.Engine.Release();
        var result = await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal([("t1", "completed"), ("t2", "completed"), ("t3", "completed")], Tasks(result).Rows);
        // t1 was not run again and t2's container was not started again: three containers in all, three handovers, one per task branch.
        Assert.Equal(3, rig.Engine.Started.Count);
        Assert.Equal([firstRun, secondRun], rig.Engine.Started.Take(2).Select(s => s.RunId));
        Assert.Equal(3, rig.Handover.Inputs.Count);
        Assert.Equal(3, rig.Handover.Inputs.Select(i => i.Branch).Distinct().Count());
        Assert.Equal($"chargehand/{secondRun}", rig.Handover.Inputs[1].Branch);
        // The egress container was made again with the batch's own key, so tokens the running session holds still work.
        Assert.Equal(2, rig.Engine.Egresses.Count);
        Assert.Equal(rig.Engine.Egresses[0].Gateway!.TokenKey, rig.Engine.Egresses[1].Gateway!.TokenKey);
        Assert.Equal(state.TokenKey, rig.Engine.Egresses[1].Gateway!.TokenKey);
        Assert.Equal(ResultStatus.Completed, (await rig.Log.ReadAsync(BatchId, default)).Run!.Result.Status);
        Assert.Empty(Directory.GetFiles(rig.StateDirectory, "*.json"));
        Assert.Contains("network-rm chargehand-net-run-batch-r", rig.Engine.Calls);
    }

    [Fact]
    public async Task A_task_whose_container_is_gone_without_output_runs_again_under_a_new_run_id()
    {
        using var rig = new DrivenRunTests.Rig(durable: true);
        var state = await StopMidBatch(rig);
        var lost = state.Tasks[1].RunId!;
        rig.Engine.Gone.Add($"cid-{lost}");
        rig.Volumes.NoOutput.Add(lost);

        var result = await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal(4, rig.Engine.Started.Count);
        Assert.Equal(lost, rig.Engine.Started[1].RunId);
        Assert.NotEqual(lost, rig.Engine.Started[2].RunId);
        Assert.Equal(3, rig.Handover.Inputs.Count);
        Assert.DoesNotContain(rig.Handover.Inputs, i => i.Branch.Contains(lost, StringComparison.Ordinal));
        Assert.Contains($"volume-rm chargehand-work-{lost}", rig.Engine.Calls);
    }

    [Fact]
    public async Task State_whose_batch_has_a_run_record_is_deleted_and_not_resumed()
    {
        using var rig = new DrivenRunTests.Rig(durable: true);
        await rig.Run.RunAsync(Request("one"), BatchId, Token, default);
        new BatchStateStore(rig.StateDirectory).Save(new BatchState(BatchId, DateTimeOffset.UtcNow, "t", Request("one"), null, [], []));

        Assert.Empty(await rig.Run.UnfinishedAsync(default));
        Assert.Empty(Directory.GetFiles(rig.StateDirectory, "*.json"));
    }

    [Fact]
    public async Task The_sweep_removes_only_chargehand_resources_that_no_resumed_batch_owns()
    {
        using var rig = new DrivenRunTests.Rig(durable: true);
        rig.Engine.Resources = new(
            ["chargehand-egress-run-b1", "chargehand-run-t1", "chargehand-run-orphan", "chargehand-egress-run-old", "chargehand-prep-run-orphan", "postgres"],
            ["chargehand-net-run-b1", "chargehand-net-run-old", "bridge"],
            ["chargehand-work-run-t1", "chargehand-out-run-t1", "chargehand-out-run-orphan", "pgdata"]);
        var kept = new BatchState("run-b1", DateTimeOffset.UtcNow, "t", Request("one"), null, [new TaskEntry("t1", null, "one", TaskPhase.Started, "run-t1")], []);

        var removed = await rig.Run.SweepOrphansAsync([kept], default);

        Assert.Equal(["chargehand-run-orphan", "chargehand-egress-run-old", "chargehand-prep-run-orphan", "chargehand-net-run-old", "chargehand-out-run-orphan"], removed);
        Assert.Equal(["rm chargehand-run-orphan", "rm chargehand-egress-run-old", "rm chargehand-prep-run-orphan", "network-rm chargehand-net-run-old", "volume-rm chargehand-out-run-orphan"],
            rig.Engine.Calls);
    }

    [Fact]
    public void A_run_token_issued_before_a_restart_validates_after_it()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "state", "run-token.key");
        var claims = new RunTokenClaims("run-1", "/srv/repo", Commit, null, 1000, DateTimeOffset.UtcNow.AddHours(1));
        var token = RunTokenService.LoadOrCreate(file).Issue(claims, DateTimeOffset.UtcNow);

        Assert.Equal(claims, RunTokenService.LoadOrCreate(file).Validate(token, DateTimeOffset.UtcNow));
        Assert.Null(new RunTokenService().Validate(token, DateTimeOffset.UtcNow));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        Assert.DoesNotContain(Convert.ToHexString(File.ReadAllBytes(file)), token);
    }

    [Fact]
    public void A_crash_mid_write_leaves_the_previous_state_whole()
    {
        using var dir = new TempDir();
        var store = new BatchStateStore(dir.Path);
        var first = new BatchState("run-a", DateTimeOffset.UtcNow, "t", Request("one"), "key", [new TaskEntry("t1", null, "one")], []);
        store.Save(first);
        // A write that died half way: the temporary file is torn, the real one was never touched.
        dir.Write("run-a.json.0123456789abcdef.tmp", "{\"run_id\":\"run-a\",\"tas");
        dir.Write("run-bad.json", "{ not json");

        var read = Assert.Single(store.List());
        Assert.Equal("key", read.TokenKey);
        Assert.Equal(TaskPhase.Pending, read.Tasks.Single().Phase);

        store.Save(first with { Tasks = [first.Tasks[0] with { Phase = TaskPhase.Started, RunId = "run-x" }] });
        Assert.Equal("run-x", store.List().Single(s => s.RunId == "run-a").Tasks.Single().RunId);
        Assert.Single(Directory.GetFiles(dir.Path, "*.tmp"));
    }
}
