using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Journal.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;
using Chargehand.Modules.Tasks.Core;

namespace Chargehand.Modules.Tasks.Tests;

/// <summary>ADR 0061, amendment of 2026-10-14: a batch's task entries are the fold of their events in the journal's <c>&lt;batch&gt;/tasks</c> flow, over the tasks as planned. These run on a real
/// journal file in a temporary directory.</summary>
public sealed class TaskJournalTests : IAsyncLifetime
{
    private const string Batch = "run-batch-1";
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly SessionOutcome Spent = new(SessionStatus.Stalled, "max_turns", 80, 1000, 200, 4000, 1.25m, 0, ["which base?"], true, Commits: 3, ModelUsage: new Dictionary<string, TokenCounts>
    {
        ["claude-sonnet-5-5"] = new(1000, 200, 0, 4000, 300),
    });
    private static readonly TaskOutcome Completed = new("t1", TaskState.Completed, "run-2", "pull request opened", null, 1.5m, 2400, "chargehand/run-1", "https://example.test/o/r/pull/1",
        Runner: "r1");
    private static readonly TaskEntry Planned = new("t1", "ref-1", "goal", From: new ContinuedFrom("run-0", new KeptWork("chargehand/run-0", "run-0", 2, "abc")), DependsOn: ["t0"], Size: "S",
        SplitFrom: "t", SizeFrom: "plan", Title: "Title");

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("chargehand-task-journal-");
    private DrivenJournal _journal = null!;

    public async Task InitializeAsync() => _journal = await DrivenJournal.OpenAsync(_dir.FullName);

    public async Task DisposeAsync()
    {
        await _journal.DisposeAsync();
        _dir.Delete(recursive: true);
    }

    /// <summary>Every event a task runner and the scheduler write, in the order of a task that is placed, begins, continues once, fails to continue a second time, is handed over and rewritten.</summary>
    private static readonly TaskEvent[] Life =
    [
        new TaskPlaced("r1"),
        new TaskBegan("run-1", "chargehand/run-1", T0, T0, new BestBundle("run-0", 2)),
        new TaskSessionStarting(T0.AddSeconds(5)),
        new TaskContainerStarted("chargehand-run-1"),
        new TaskSessionRead(),
        new TaskContinuationBegan("run-2", ["run-1", "run-2"], 1, Spent, T0.AddHours(1), new BestBundle("run-1", 3), "goal again", "continuation 1 of 2"),
        new TaskRunRenamed("run-3", ["run-1", "run-3"]),
        new TaskContainerStarted("chargehand-run-3"),
        new TaskSessionRead(),
        new TaskContinuationBegan("run-4", ["run-1", "run-3", "run-4"], 2, Spent, T0.AddHours(2), new BestBundle("run-3", 4), "goal once more", "continuation 2 of 2"),
        new TaskContinuationFailed("run-3", ["run-1", "run-3"], 1),
        new TaskFinished(Completed),
        new TaskFinished(Completed with { Detail = "rewritten for the cap" }),
    ];

    [Fact]
    public async Task Each_change_is_an_event_in_the_tasks_flow_and_reopening_folds_the_same_entry()
    {
        var tasks = await TaskJournal.OpenAsync(_journal, Batch, [Planned, new TaskEntry("t2", null, "two")]);
        var expected = Planned;
        foreach (var e in Life)
        {
            await tasks.RecordAsync("t1", e);
            expected = TaskMachine.Decide(expected, e).State;
        }

        var reopened = await TaskJournal.OpenAsync(_journal, Batch, [Planned, new TaskEntry("t2", null, "two")]);

        Assert.Equal(Shown(expected), Shown(tasks.Entry("t1")!));
        Assert.Equal(Shown(expected), Shown(reopened.Entry("t1")!));
        Assert.Equal(TaskPhase.Pending, reopened.Entry("t2")!.Phase);
        Assert.Equal(["t1", "t2"], reopened.Entries.Select(e => e.Id));
        var events = await _journal.ReadEventsAsync(TaskJournal.FlowOf(Batch));
        Assert.Equal(["placed", "began", "session_starting", "container_started", "session_read", "continuation_began", "run_renamed", "container_started", "session_read", "continuation_began",
            "continuation_failed", "finished", "finished"], events.Select(e => e.Kind));
        Assert.All(events, e => Assert.StartsWith("{\"task\":\"t1\"", e.Payload, StringComparison.Ordinal));
        Assert.Empty(await _journal.ReadEventsAsync(Batch));
    }

    [Fact]
    public async Task An_event_is_written_in_snake_case_with_the_task_first()
    {
        var tasks = await TaskJournal.OpenAsync(_journal, Batch, [Planned]);

        await tasks.FinishedAsync(new TaskOutcome("t1", TaskState.NeedsInput, "run-1", "asked", ErrorCode.Cancelled));

        var e = Assert.Single(await _journal.ReadEventsAsync(TaskJournal.FlowOf(Batch)));
        Assert.Equal(("finished", """{"task":"t1","outcome":{"id":"t1","state":"needs_input","run_id":"run-1","detail":"asked","error":"cancelled","usd":0,"tokens":0,"replace":false}}"""),
            (e.Kind, e.Payload));
    }

    [Fact]
    public async Task The_schedulers_writes_place_replace_and_end_a_task()
    {
        var tasks = await TaskJournal.OpenAsync(_journal, Batch, [Planned]);

        await tasks.PlacedAsync("t1", "r2");
        Assert.Equal("r2", tasks.Entry("t1")!.Runner);
        await tasks.RecordAsync("t1", new TaskBegan("run-1", "chargehand/run-1", T0, T0, null));
        await tasks.ReplacedAsync("t1");
        Assert.Equal(Shown(Planned), Shown(tasks.Entry("t1")!));
        Assert.Null(tasks.Ended("t1"));
        await tasks.FinishedAsync(Completed);

        Assert.Equal(Completed, tasks.Ended("t1"));
        Assert.Equal(Completed, (await TaskJournal.OpenAsync(_journal, Batch, [Planned])).Ended("t1"));
    }

    /// <summary>The flow is open to events a later slice adds (the scheduler's operator cancels, admissions and ends, each with the time it decided at): a kind the fold does not know is skipped, and
    /// so is an event for a task the batch does not have or one that does not parse.</summary>
    [Fact]
    public async Task A_kind_it_does_not_know_a_task_it_does_not_have_and_a_torn_payload_are_skipped()
    {
        var tasks = await TaskJournal.OpenAsync(_journal, Batch, [Planned]);
        await tasks.PlacedAsync("t1", "r1");
        await _journal.AppendEventAsync(TaskJournal.FlowOf(Batch), "operator_cancelled", """{"task":"t1","now":"2026-10-14T12:00:00+00:00"}""");
        await _journal.AppendEventAsync(TaskJournal.FlowOf(Batch), "placed", """{"task":"t9","runner":"r9"}""");
        await _journal.AppendEventAsync(TaskJournal.FlowOf(Batch), "placed", """{"task":"t1","runn""");
        await tasks.RecordAsync("t1", new TaskBegan("run-1", "chargehand/run-1", T0, T0, null));

        var reopened = await TaskJournal.OpenAsync(_journal, Batch, [Planned]);

        Assert.Equal(Shown(tasks.Entry("t1")!), Shown(reopened.Entry("t1")!));
        Assert.Equal(("r1", TaskPhase.Started), (reopened.Entry("t1")!.Runner, reopened.Entry("t1")!.Phase));
        Assert.Null(reopened.Entry("t9"));
    }

    [Fact]
    public async Task An_event_the_tasks_phase_does_not_take_is_refused_and_not_written()
    {
        var tasks = await TaskJournal.OpenAsync(_journal, Batch, [Planned]);
        await tasks.FinishedAsync(Completed);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => tasks.PlacedAsync("t1", "r2"));

        Assert.Contains("TaskPlaced does not apply to task t1, which is ended", refused.Message, StringComparison.Ordinal);
        Assert.Equal(["finished"], (await _journal.ReadEventsAsync(TaskJournal.FlowOf(Batch))).Select(e => e.Kind));
        Assert.Null(tasks.Entry("t1")!.Runner);
    }

    /// <summary>A flow written by hand or by a bug: an event its phase does not take is skipped on replay, and the ones after it fold.</summary>
    [Fact]
    public async Task A_refused_event_in_the_flow_is_skipped_on_replay()
    {
        await _journal.AppendEventAsync(TaskJournal.FlowOf(Batch), "container_started", """{"task":"t1","container":"chargehand-run-1"}""");
        await _journal.AppendEventAsync(TaskJournal.FlowOf(Batch), "placed", """{"task":"t1","runner":"r1"}""");

        var tasks = await TaskJournal.OpenAsync(_journal, Batch, [Planned]);

        Assert.Equal(Shown(Planned with { Runner = "r1" }), Shown(tasks.Entry("t1")!));
    }

    [Fact]
    public async Task A_task_the_batch_does_not_have_changes_nothing_and_writes_nothing()
    {
        var tasks = await TaskJournal.OpenAsync(_journal, Batch, [Planned]);

        await tasks.PlacedAsync("t9", "r1");

        Assert.Empty(await _journal.ReadEventsAsync(TaskJournal.FlowOf(Batch)));
    }

    [Fact]
    public async Task The_tasks_fold_over_what_planning_gave_them_whatever_else_the_entries_handed_in_carry()
    {
        var tasks = await TaskJournal.OpenAsync(_journal, Batch, [Planned with { Phase = TaskPhase.HandedOver, RunId = "run-1", Outcome = Completed, Runner = "r1" }]);

        Assert.Equal(Shown(Planned), Shown(tasks.Entry("t1")!));
    }

    /// <summary>One release only: an entry a snapshot written before the task journal held, imported as one event per task; a cut import repeats to the same entries.</summary>
    [Fact]
    public async Task An_imported_entry_stands_for_the_whole_entry_and_importing_again_changes_nothing()
    {
        var saved = Planned with
        {
            Phase = TaskPhase.Started,
            RunId = "run-2",
            Branch = "chargehand/run-1",
            Container = "chargehand-run-2",
            Started = T0,
            Continuation = 1,
            Prior = Spent,
            Runs = ["run-1", "run-2"],
            SessionStarted = T0.AddHours(1),
            Best = new BestBundle("run-1", 3),
            SessionGoal = "goal again",
            StartDetail = "continuation 1 of 2",
            Runner = "r1",
        };
        var done = new TaskEntry("t2", null, "two", TaskPhase.HandedOver, "run-9", Outcome: Completed with { Id = "t2" });

        await TaskJournal.ImportAsync(_journal, Batch, [saved]);
        await TaskJournal.ImportAsync(_journal, Batch, [saved, done]);
        var tasks = await TaskJournal.OpenAsync(_journal, Batch, [Planned, new TaskEntry("t2", null, "two")]);

        Assert.Equal(Shown(saved), Shown(tasks.Entry("t1")!));
        Assert.Equal(Shown(done), Shown(tasks.Entry("t2")!));
        Assert.Equal(["imported", "imported", "imported"], (await _journal.ReadEventsAsync(TaskJournal.FlowOf(Batch))).Select(e => e.Kind));
    }

    [Fact]
    public async Task Forgetting_a_batch_removes_its_task_events()
    {
        var tasks = await TaskJournal.OpenAsync(_journal, Batch, [Planned]);
        await tasks.PlacedAsync("t1", "r1");

        await TaskJournal.ForgetAsync(_journal, Batch);

        Assert.Empty(await _journal.ReadEventsAsync(TaskJournal.FlowOf(Batch)));
        Assert.Equal(Shown(Planned), Shown((await TaskJournal.OpenAsync(_journal, Batch, [Planned])).Entry("t1")!));
    }

    /// <summary>The entry with its lists and dictionaries as JSON: record equality compares those by reference.</summary>
    private static string Shown(TaskEntry e) => System.Text.Json.JsonSerializer.Serialize(e, ContractJson.Options);
}
