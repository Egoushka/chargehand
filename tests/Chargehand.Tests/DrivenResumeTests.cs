using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Journal.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;
using Chargehand.Server;
using Chargehand.Verification;

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
    private static async Task<BatchState> StopMidBatch(DrivenRunTests.Rig rig, string? actor = null)
    {
        rig.Engine.HoldNth = 2;
        using var stop = new CancellationTokenSource();
        var first = rig.Run.RunAsync(Request("one", "two", "three"), BatchId, Token, stop.Token, null, () => false, actor: actor);
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
    public async Task A_batch_and_its_tasks_carry_the_account_and_a_resumed_batch_keeps_it()
    {
        using var rig = new DrivenRunTests.Rig(durable: true, accounts: [new("alice", Driven: true, AllowServerCredential: true)]);
        var state = await StopMidBatch(rig, "alice");
        Assert.Equal("alice", state.Actor);
        Assert.Equal("alice", (await rig.Log.ReadAsync(BatchId, default)).Start!.Actor);

        rig.Engine.Release();
        // The server resumes it without naming anyone: the saved state does.
        var result = await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        Assert.Equal("alice", result.Actor);
        var entry = await rig.Log.ReadAsync(BatchId, default);
        Assert.Equal(("alice", "alice"), (entry.Start!.Actor, entry.Run!.Actor));
        Assert.Equal("alice", entry.Run.Result.Actor);
        foreach (var started in rig.Engine.Started)
        {
            var task = await rig.Log.ReadAsync(started.RunId, default);
            Assert.Equal(("alice", "alice", "alice"), (task.Start!.Actor, task.Run!.Actor, task.Run.Result.Actor));
        }
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
    public async Task A_batch_records_its_workspace_and_task_file_steps_and_forgets_them_when_its_run_record_is_written()
    {
        using var rig = new DrivenRunTests.Rig(durable: true);
        var state = await StopMidBatch(rig);
        var steps = state.Tasks.Take(2).SelectMany(t => new[] { StepKey.Prep(BatchId, t.RunId!), StepKey.Transfer(BatchId, t.RunId!, "task") }).ToList();

        // The journal is a second connection to the server's file: a key that is already completed does not run, one that is not recorded does.
        using (var journal = await DrivenJournal.OpenAsync(rig.StateDirectory))
        {
            Assert.Equal(0, await RunAll(journal, steps));
            Assert.Empty(await journal.UnfinishedAsync());
        }

        rig.Engine.Release();
        var result = await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        Assert.Equal(ResultStatus.Completed, result.Status);
        using (var journal = await DrivenJournal.OpenAsync(rig.StateDirectory))
            Assert.Equal(steps.Count, await RunAll(journal, steps));
    }

    /// <summary>t1 handed over, then t2's session start is cut after docker made its container and before it started it, as the server stops (ADR 0061). Its workspace and task file were done.</summary>
    private static async Task<(BatchState State, string Cut)> CutMidStart(DrivenRunTests.Rig rig)
    {
        using var stop = new CancellationTokenSource();
        rig.Engine.OnStart = n =>
        {
            if (n != 2)
                return null;
            rig.Engine.Created.Add($"chargehand-{rig.Engine.Started[1].RunId}");
            stop.Cancel();
            return new OperationCanceledException(stop.Token);
        };
        await rig.Run.RunAsync(Request("one", "two", "three"), BatchId, Token, stop.Token, null, () => false);
        rig.Engine.OnStart = null;

        var state = Assert.Single(await rig.Restart().UnfinishedAsync(default));
        var cut = state.Tasks[1].RunId!;
        Assert.Equal([TaskPhase.HandedOver, TaskPhase.Started, TaskPhase.Pending], state.Tasks.Select(t => t.Phase));
        Assert.Equal(rig.Engine.Started[1].RunId, cut);
        using (var journal = await DrivenJournal.OpenAsync(rig.StateDirectory))
            Assert.Equal([StepKey.Session(BatchId, "t2", cut)], await journal.UnfinishedAsync());
        return (state, cut);
    }

    [Fact]
    public async Task A_session_start_cut_after_the_container_was_made_starts_again_under_the_same_run_id_without_preparing_again()
    {
        using var rig = new DrivenRunTests.Rig(durable: true);
        var (state, cut) = await CutMidStart(rig);

        var result = await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal([("t1", "completed"), ("t2", "completed"), ("t3", "completed")], Tasks(result).Rows);
        // The made container was removed by its name, then started again under the same run id: t2 has one run, one branch and one run record.
        Assert.Equal([state.Tasks[0].RunId, cut, cut], rig.Engine.Started.Take(3).Select(s => s.RunId));
        Assert.Equal(4, rig.Engine.Started.Count);
        Assert.Empty(rig.Engine.Created);
        Assert.True(rig.Engine.Calls.IndexOf($"rm chargehand-{cut}") < rig.Engine.Calls.LastIndexOf($"start {cut}"));
        Assert.Equal($"chargehand/{cut}", rig.Handover.Inputs[1].Branch);
        Assert.NotNull((await rig.Log.ReadAsync(cut, default)).Run);
        // The workspace and the task file finished before the cut, so they are not done again.
        Assert.Single(rig.Engine.Calls, c => c.StartsWith($"prepare {cut} ", StringComparison.Ordinal));
        Assert.Single(rig.Volumes.Tasks, t => t.RunId == cut);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public async Task A_session_start_cut_after_the_container_started_takes_that_container_up(int runFor)
    {
        using var rig = new DrivenRunTests.Rig(durable: true);
        var (state, cut) = await CutMidStart(rig);
        // Docker started it after all; it still runs (2), or it ended and left its outcome (0).
        rig.Engine.Created.Clear();
        rig.Engine.RunFor = runFor;

        var result = await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal(3, rig.Engine.Started.Count);
        Assert.Equal([state.Tasks[0].RunId, cut], rig.Engine.Started.Take(2).Select(s => s.RunId));
        Assert.NotEqual(cut, rig.Engine.Started[2].RunId);
        Assert.Equal($"chargehand/{cut}", rig.Handover.Inputs[1].Branch);
        Assert.Single(rig.Engine.Calls, c => c.StartsWith($"prepare {cut} ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_session_start_cut_whose_container_ran_and_ended_without_an_outcome_starts_again_on_a_fresh_workspace()
    {
        using var rig = new DrivenRunTests.Rig(durable: true);
        var (state, cut) = await CutMidStart(rig);
        // Docker started it after all and the session died without an outcome: it may have left commits in the work volume.
        rig.Engine.Created.Clear();
        rig.Volumes.NoOutput.Add(cut);
        rig.Engine.OnStart = _ =>
        {
            rig.Volumes.NoOutput.Remove(cut);
            return null;
        };

        var result = await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal([state.Tasks[0].RunId, cut, cut], rig.Engine.Started.Take(3).Select(s => s.RunId));
        Assert.Equal($"chargehand/{cut}", rig.Handover.Inputs[1].Branch);
        // The dead session's container and work volume go, then the workspace is prepared again before the session starts again under the same run id.
        var calls = rig.Engine.Calls;
        var removed = calls.IndexOf($"rm chargehand-{cut}");
        var volumeRemoved = calls.IndexOf($"volume-rm chargehand-work-{cut}", removed);
        var prepared = calls.FindLastIndex(c => c.StartsWith($"prepare {cut} ", StringComparison.Ordinal));
        Assert.True(removed >= 0 && removed < volumeRemoved && volumeRemoved < prepared && prepared < calls.LastIndexOf($"start {cut}"), string.Join("\n", calls));
        Assert.Equal(2, calls.Count(c => c.StartsWith($"prepare {cut} ", StringComparison.Ordinal)));
        Assert.Single(rig.Volumes.Tasks, t => t.RunId == cut);
    }

    private static async Task<int> RunAll(DrivenJournal journal, IEnumerable<StepKey> keys)
    {
        var ran = 0;
        foreach (var key in keys)
            await journal.RunAsync(key, (_, _) =>
            {
                ran++;
                return Task.CompletedTask;
            });
        return ran;
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
        Assert.Equal(["list", "rm chargehand-run-orphan", "rm chargehand-egress-run-old", "rm chargehand-prep-run-orphan", "network-rm chargehand-net-run-old", "volume-rm chargehand-out-run-orphan"],
            rig.Engine.Calls);
    }

    [Fact]
    public async Task The_sweep_removes_a_finished_tasks_session_container_and_work_volume_and_keeps_its_output_volume()
    {
        using var rig = new DrivenRunTests.Rig(durable: true);
        // A kill between t1's saved outcome and its teardown left its container and work volume; t2 still runs.
        rig.Engine.Resources = new(["chargehand-run-t1", "chargehand-run-t2"], [], ["chargehand-work-run-t1", "chargehand-out-run-t1", "chargehand-work-run-t2", "chargehand-out-run-t2"]);
        var kept = new BatchState("run-b1", DateTimeOffset.UtcNow, "t", Request("one", "two"), null,
            [new TaskEntry("t1", null, "one", TaskPhase.HandedOver, "run-t1"), new TaskEntry("t2", null, "two", TaskPhase.Started, "run-t2")], []);

        var removed = await rig.Run.SweepOrphansAsync([kept], default);

        Assert.Equal(["chargehand-run-t1", "chargehand-work-run-t1"], removed);
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

    private sealed class CancelsWhenResolving(CancellationTokenSource stop) : IEvidenceResolver
    {
        public Task<IReadOnlyList<EvidenceFailure>> ResolveAsync(ResultContract contract, EvidenceScope scope, CancellationToken ct)
        {
            stop.Cancel();
            return Task.FromResult<IReadOnlyList<EvidenceFailure>>([]);
        }
    }

    [Fact]
    public async Task A_task_that_finished_as_the_server_stopped_is_cleaned_up_and_saved_as_done()
    {
        using var stop = new CancellationTokenSource();
        using var rig = new DrivenRunTests.Rig(durable: true, resolver: new CancelsWhenResolving(stop));
        await rig.Run.RunAsync(Request("one"), BatchId, Token, stop.Token, null, () => false);

        var runId = rig.Engine.Started.Single().RunId;
        Assert.Contains($"rm cid-{runId}", rig.Engine.Calls);
        Assert.Contains($"volume-rm chargehand-work-{runId}", rig.Engine.Calls);
        var saved = Assert.Single(await rig.Restart().UnfinishedAsync(default)).Tasks.Single();
        Assert.Equal(TaskPhase.HandedOver, saved.Phase);
    }

    [Fact]
    public async Task Resuming_sweeps_before_it_starts_a_batch_and_a_failing_engine_keeps_the_server_up()
    {
        using var rig = new DrivenRunTests.Rig(durable: true);
        var state = await StopMidBatch(rig);
        rig.Engine.Release();
        using var runs = new RunService(null!, "", CancellationToken.None, rig.Run);
        var before = rig.Engine.Calls.Count;

        await runs.ResumeAsync(default);
        for (var i = 0; i < 400 && (await rig.Log.ReadAsync(state.RunId, default)).Run is null; i++)
            await Task.Delay(25);

        Assert.Equal("list", rig.Engine.Calls[before]);
        Assert.Equal(ResultStatus.Completed, (await rig.Log.ReadAsync(state.RunId, default)).Run!.Result.Status);
        // The first start came from before the stop; the resume writes its own, so the log names the process that finished the batch.
        var starts = (await rig.Log.ReadAllAsync(default)).Starts.Where(s => s.RunId == state.RunId).ToList();
        Assert.Equal(2, starts.Count);
        Assert.Equal(ProcessBoot.Id, starts[^1].BootId);

        rig.Engine.OnList = new InvalidOperationException("the runner is down");
        using var failing = new RunService(null!, "", CancellationToken.None, rig.Run);
        await new BatchResumer(failing).StartAsync(default);
    }

    [Fact]
    public void Damaged_key_and_state_files_are_replaced_or_skipped_not_fatal()
    {
        using var dir = new TempDir();
        foreach (var junk in new[] { "not hex at all", "abc" })
        {
            var file = dir.Write("k/run-token.key", junk);
            var service = RunTokenService.LoadOrCreate(file);
            var claims = new RunTokenClaims("run-1", "/srv/repo", Commit, null, 1000, DateTimeOffset.UtcNow.AddHours(1));
            Assert.NotNull(service.Validate(service.Issue(claims, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow));
        }
        dir.Write("s/run-empty.json", "{}");
        dir.Write("s/run-blank.json", """{"run_id":""}""");
        dir.Write("s/run-torn.json", "{\"run_id\":\"run-t");
        var store = new BatchStateStore(Path.Combine(dir.Path, "s"));
        store.Save(new BatchState("run-ok", DateTimeOffset.UtcNow, "t", Request("one"), null, [], []));
        Assert.Equal("run-ok", Assert.Single(store.List()).RunId);
    }
}
