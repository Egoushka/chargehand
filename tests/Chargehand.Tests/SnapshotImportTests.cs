using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Journal.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>A batch in flight at the deploy that moved task state into the journal (ADR 0061, amendment of 2026-10-14). <c>SnapshotImport/run-batch-fixture.json</c> is the snapshot the code before
/// it wrote (main at <c>a8abb6b</c>) for three tasks run one at a time, stopped with t1 handed over, t2's session running and t3 not started; only its token key was replaced by a placeholder. It is resumed on a fresh
/// server: an empty journal and an engine that knows no container, so t2's container is found exited. The first test was written against that code and passes on it unchanged.</summary>
public class SnapshotImportTests
{
    private const string BatchId = "run-batch-fixture";
    private const string FirstRun = "run-20261014-095508-cc6bea";
    private const string SecondRun = "run-20261014-095508-ff2181";

    private static string Fixture => Path.Combine(Repo.Root, "tests", "Chargehand.Tests", "SnapshotImport", BatchId + ".json");

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private static DrivenRunTests.Rig WithFixture()
    {
        var rig = new DrivenRunTests.Rig(durable: true);
        Directory.CreateDirectory(rig.StateDirectory);
        File.Copy(Fixture, Path.Combine(rig.StateDirectory, BatchId + ".json"));
        return rig;
    }

    [Fact]
    public async Task A_snapshot_written_before_the_task_journal_resumes_as_it_did()
    {
        using var rig = WithFixture();

        var state = Assert.Single(await rig.Run.UnfinishedAsync(default));
        Assert.Equal([TaskPhase.HandedOver, TaskPhase.Started, TaskPhase.Pending], state.Tasks.Select(t => t.Phase));
        Assert.Equal([FirstRun, SecondRun, null], state.Tasks.Select(t => t.RunId));
        // t2's session ended while the server was down: its output volume outlived the old process with its task file.
        await rig.Volumes.WriteTaskAsync(SecondRun, new SessionTask(SecondRun, "two", $"chargehand/{SecondRun}", 80, 60, 600, 1_000_000), default);
        var result = await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        Assert.Equal(ResultStatus.Completed, result.Status);
        var rows = System.Text.Json.JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "driven-batch").Content!).RootElement.GetProperty("tasks").EnumerateArray()
            .Select(t => (t.GetProperty("id").GetString(), t.GetProperty("run_id").GetString(), t.GetProperty("status").GetString(), t.GetProperty("pr_url").GetString())).ToList();
        // t1 is not run again, t2 is taken up on the session it had, and t3 starts: one container, two handovers.
        var started = Assert.Single(rig.Engine.Started).RunId;
        Assert.Equal([("t1", FirstRun, "completed", "https://example.test/o/r/pull/1"), ("t2", SecondRun, "completed", "https://example.test/o/r/pull/1"),
            ("t3", started, "completed", "https://example.test/o/r/pull/2")], rows);
        Assert.Equal([$"chargehand/{SecondRun}", $"chargehand/{started}"], rig.Handover.Inputs.Select(i => i.Branch));
        Assert.Equal([
            "rm chargehand-egress-run-batch-fixture", "network chargehand-net-run-batch-fixture", "egress run-batch-fixture []", "connect egress-1 bridge",
            $"rm cid-{SecondRun}", $"volume-rm chargehand-work-{SecondRun}",
            $"volume chargehand-out-{started}", $"volume-rm chargehand-work-{started}", $"prepare {started} chargehand/{started} abc1234def5678abc1234def5678abc1234def56", $"start {started}",
            $"rm cid-{started}", $"volume-rm chargehand-work-{started}", "rm egress-1", "network-rm chargehand-net-run-batch-fixture",
        ], rig.Engine.Calls);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Assert.Single(rig.Engine.Egresses).Gateway!.TokenKey);
        Assert.Equal(ResultStatus.Completed, (await rig.Log.ReadAsync(BatchId, default)).Run!.Result.Status);
        Assert.Empty(Directory.GetFiles(rig.StateDirectory, "*.json"));
    }

    [Fact]
    public async Task The_import_keeps_the_snapshot_puts_each_tasks_entry_into_the_journal_and_saves_the_tasks_as_planned()
    {
        using var rig = WithFixture();

        var state = Assert.Single(await rig.Run.UnfinishedAsync(default));

        var kept = Path.Combine(rig.StateDirectory, BatchId + ".imported");
        Assert.Equal(File.ReadAllText(Fixture), File.ReadAllText(kept));
        var saved = Assert.Single(new BatchStateStore(rig.StateDirectory).List());
        Assert.Equal(TaskJournal.FlowOf(BatchId), saved.TaskFlow);
        Assert.All(saved.Tasks, t => Assert.Equal((TaskPhase.Pending, null, null), (t.Phase, t.RunId, t.Outcome)));
        Assert.Equal(state.TokenKey, saved.TokenKey);
        await using (var journal = await DrivenJournal.OpenAsync(rig.StateDirectory))
            Assert.Equal(["imported", "imported", "imported"], (await journal.ReadEventsAsync(TaskJournal.FlowOf(BatchId))).Select(e => e.Kind));
        // A second start finds the snapshot imported and folds the same entries.
        var again = Assert.Single(await rig.Restart().UnfinishedAsync(default));
        Assert.Equal(state.Tasks.Select(Shown), again.Tasks.Select(Shown));
        Assert.Equal(File.ReadAllText(Fixture), File.ReadAllText(kept));

        await rig.Volumes.WriteTaskAsync(SecondRun, new SessionTask(SecondRun, "two", $"chargehand/{SecondRun}", 80, 60, 600, 1_000_000), default);
        Assert.Equal(ResultStatus.Completed, (await rig.Run.RunAsync(again.Request, again.RunId, Token, default, null, () => false, again)).Status);
        Assert.Empty(Directory.GetFiles(rig.StateDirectory, BatchId + ".*"));
    }

    /// <summary>A start cut inside the import, after the snapshot was kept and one task's entry went into the journal: the next start imports again and folds the same entries.</summary>
    [Fact]
    public async Task An_import_cut_short_repeats_to_the_same_entries()
    {
        using var whole = WithFixture();
        var expected = Assert.Single(await whole.Run.UnfinishedAsync(default)).Tasks.Select(Shown).ToList();
        using var rig = WithFixture();
        File.Copy(Fixture, Path.Combine(rig.StateDirectory, BatchId + ".imported"));
        var before = Assert.Single(new BatchStateStore(rig.StateDirectory).List());
        await using (var journal = await DrivenJournal.OpenAsync(rig.StateDirectory))
            await TaskJournal.ImportAsync(journal, BatchId, before.Tasks.Take(1));

        var state = Assert.Single(await rig.Run.UnfinishedAsync(default));

        Assert.Equal(expected, state.Tasks.Select(Shown));
    }

    private static string Shown(TaskEntry e) => System.Text.Json.JsonSerializer.Serialize(e, ContractJson.Options);
}
