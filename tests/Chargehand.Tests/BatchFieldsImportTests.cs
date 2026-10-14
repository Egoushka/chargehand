using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Journal.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>A batch in flight at the deploy that moved the batch's own fields out of the snapshot (ADR 0061, amendment of 2026-10-14, the second). <c>SnapshotImport/batch-fields/</c> is what the
/// code before it (main at <c>8e43c5e</c>) left on disk for a planned batch of three tasks run one at a time, started by an account and stopped with big-1 handed over, big's session running and
/// other not started; a fourth task did not resolve. It holds the snapshot (its token key replaced by a placeholder), the journal and the run log. It is resumed on a fresh server whose engine knows
/// no container, so big's container is found exited. The first test was written against that code and passes on it unchanged.</summary>
public class BatchFieldsImportTests
{
    private const string BatchId = "run-batch-fixture-s7";
    private const string FirstRun = "run-20261014-113433-39759f";
    private const string SecondRun = "run-20261014-113434-0f177f";
    private const string TokenKey = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static string Fixture(string name) => Path.Combine(Repo.Root, "tests", "Chargehand.Tests", "SnapshotImport", "batch-fields", name);

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private sealed class NoPlanner : IBatchPlanner
    {
        public int Calls;

        public Task<PlanProposal> ProposeAsync(PlanInput input, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromException<PlanProposal>(new PlanException("a resumed batch is not planned again"));
        }
    }

    private static DrivenRunTests.Rig WithFixture(NoPlanner planner)
    {
        var rig = new DrivenRunTests.Rig(durable: true, planner: planner, stacker: new DrivenDependsOnTests.FakeStacker(), accounts: [new("alice", Driven: true, AllowServerCredential: true)]);
        Directory.CreateDirectory(rig.StateDirectory);
        foreach (var name in new[] { BatchId + ".json", "journal.db" })
            File.Copy(Fixture(name), Path.Combine(rig.StateDirectory, name));
        File.Copy(Fixture("log.jsonl"), Path.Combine(rig.Dir.Path, "log.jsonl"));
        return rig;
    }

    private static List<(string?, string?, string?, string?)> Rows(ResultContract result) =>
        [.. JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "driven-batch").Content!).RootElement.GetProperty("tasks").EnumerateArray()
            .Select(t => (t.GetProperty("id").GetString(), t.TryGetProperty("run_id", out var run) ? run.GetString() : null, t.GetProperty("status").GetString(),
                t.TryGetProperty("pr_url", out var pr) ? pr.GetString() : null))];

    [Fact]
    public async Task A_snapshot_with_the_batchs_fields_resumes_as_it_did()
    {
        var planner = new NoPlanner();
        using var rig = WithFixture(planner);

        var state = Assert.Single(await rig.Run.UnfinishedAsync(default));
        Assert.Equal((BatchId, "alice"), (state.RunId, state.Actor));
        Assert.Equal(["big-1", "big", "other"], state.Tasks.Select(t => t.Id));
        Assert.Equal([TaskPhase.HandedOver, TaskPhase.Started, TaskPhase.Pending], state.Tasks.Select(t => t.Phase));
        Assert.Equal([FirstRun, SecondRun, null], state.Tasks.Select(t => t.RunId));
        Assert.Equal(("bad", TaskState.Failed), (Assert.Single(state.Unresolved).Id, state.Unresolved[0].State));
        Assert.True(state.Plan!.Applied);
        // big's session ended while the server was down: its output volume outlived the old process with its task file.
        await rig.Volumes.WriteTaskAsync(SecondRun, new SessionTask(SecondRun, "second half", $"chargehand/{SecondRun}", 80, 60, 600, 1_000_000), default);
        var result = await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        Assert.Equal((ResultStatus.Failed, "alice", "1 of 4 tasks did not produce a pull request"), (result.Status, result.Actor, result.Summary));
        var started = Assert.Single(rig.Engine.Started).RunId;
        Assert.Equal([("big-1", FirstRun, "completed", "https://example.test/o/r/pull/1"), ("big", SecondRun, "completed", "https://example.test/o/r/pull/1"),
            ("other", started, "completed", "https://example.test/o/r/pull/2"), ("bad", null, "failed", null)], Rows(result));
        Assert.Equal([$"chargehand/{SecondRun}", $"chargehand/{started}"], rig.Handover.Inputs.Select(i => i.Branch));
        Assert.Equal([
            $"rm chargehand-egress-{BatchId}", $"network chargehand-net-{BatchId}", $"egress {BatchId} []", "connect egress-1 bridge",
            $"rm cid-{SecondRun}", $"volume-rm chargehand-work-{SecondRun}",
            $"volume chargehand-out-{started}", $"volume-rm chargehand-work-{started}", $"prepare {started} chargehand/{started} abc1234def5678abc1234def5678abc1234def56", $"start {started}",
            $"rm cid-{started}", $"volume-rm chargehand-work-{started}", "rm egress-1", $"network-rm chargehand-net-{BatchId}",
        ], rig.Engine.Calls);
        Assert.Equal(TokenKey, Assert.Single(rig.Engine.Egresses).Gateway!.TokenKey);
        Assert.Equal("applied", JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "plan").Content!).RootElement.GetProperty("status").GetString());
        Assert.Equal(0, planner.Calls);
        Assert.Equal(ResultStatus.Failed, (await rig.Log.ReadAsync(BatchId, default)).Run!.Result.Status);
        Assert.Empty(Directory.GetFiles(rig.StateDirectory, "*.json"));
    }

    private static string Shown(OpenBatch b) => JsonSerializer.Serialize(b, ContractJson.Options);

    [Fact]
    public async Task The_import_opens_the_batchs_step_writes_its_key_file_and_keeps_the_snapshot_and_a_later_start_reads_the_journal_alone()
    {
        using var rig = WithFixture(new NoPlanner());

        var state = Assert.Single(await rig.Run.UnfinishedAsync(default));

        // The live view folds the tasks at the start, before this process runs the batch.
        Assert.Equal([TaskPhase.HandedOver, TaskPhase.Started, TaskPhase.Pending], Assert.Single(rig.Run.LiveBatches()).Tasks.Select(t => t.Phase));
        Assert.Equal(File.ReadAllText(Fixture(BatchId + ".json")), File.ReadAllText(Path.Combine(rig.StateDirectory, BatchId + ".imported")));
        Assert.Empty(Directory.GetFiles(rig.StateDirectory, "*.json"));
        var key = TokenKeyFile.PathOf(rig.StateDirectory, BatchId);
        Assert.Equal(TokenKey, File.ReadAllText(key));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(key));
        await using (var journal = await DrivenJournal.OpenAsync(rig.StateDirectory))
        {
            Assert.Equal([StepKey.BatchStep(BatchId)], await journal.UnfinishedAsync(BatchId));
            Assert.DoesNotContain(TokenKey, string.Join("\n", (await journal.ReadEventsAsync(TaskJournal.FlowOf(BatchId))).Select(e => e.Payload)), StringComparison.Ordinal);
        }
        // Without the kept snapshot a second start reads the same batch: its fields are the journal's and the start record's.
        File.Delete(Path.Combine(rig.StateDirectory, BatchId + ".imported"));
        var again = Assert.Single(await rig.Restart().UnfinishedAsync(default));
        Assert.Equal(Shown(state), Shown(again));
        Assert.Equal(-1, (await File.ReadAllBytesAsync(Path.Combine(rig.StateDirectory, DrivenJournal.FileName))).AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(TokenKey)));
    }

    /// <summary>A start cut inside the import, after the key file was written and the batch's step opened: the next start imports again to the same batch.</summary>
    [Fact]
    public async Task An_import_cut_short_repeats_to_the_same_batch()
    {
        using var whole = WithFixture(new NoPlanner());
        var expected = Shown(Assert.Single(await whole.Run.UnfinishedAsync(default)));
        using var rig = WithFixture(new NoPlanner());
        File.WriteAllText(TokenKeyFile.PathOf(rig.StateDirectory, BatchId), TokenKey);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(TokenKeyFile.PathOf(rig.StateDirectory, BatchId), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await using (var journal = await DrivenJournal.OpenAsync(rig.StateDirectory))
            await journal.BeginAsync(StepKey.BatchStep(BatchId));

        var state = Assert.Single(await rig.Run.UnfinishedAsync(default));

        Assert.Equal(expected, Shown(state));
        Assert.Equal(expected, Shown(Assert.Single(await rig.Restart().UnfinishedAsync(default))));
    }
}
