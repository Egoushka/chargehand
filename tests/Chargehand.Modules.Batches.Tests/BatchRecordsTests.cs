using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Journal.Api;
using Chargehand.Modules.Tasks.Api;

using Chargehand.Tests;

namespace Chargehand.Modules.Batches.Tests;

/// <summary>CHARGEHAND-214: what the console reads about a batch and the run log does not keep, one directory per batch beside the run log: its events, and a line per session, governor log,
/// check snapshot and CI fix attempt of its tasks. Append-only, bounded, and pruned with the oldest batches.</summary>
public class BatchRecordsTests
{
    private static ResultContract Result(string runId) =>
        new("result/v1", runId, "session", new string('0', 32), new PromptChain([], new AsSent("claude-code/2.1.283", "session", "m", "2026-10-15")), ResultStatus.Completed, "done", [], [], [], [], 0.7,
            new Usage(1000, 200, 3000, 400, null));

    private static RunStatus Event(string batch, RunEventKind kind, string? task = null, string? detail = null, long? tokens = null) =>
        RunStatus.Of(batch, RunState.Running, kind) with { TaskId = task, Detail = detail, Tokens = tokens };

    [Fact]
    public void Events_are_read_back_in_order_without_the_result_and_with_a_long_detail_cut()
    {
        using var dir = new TempDir();
        var records = new BatchRecords(dir.Path);
        var result = Result("run-1");
        records.AppendEvent("run-1", Event("run-1", RunEventKind.Started));
        records.AppendEvent("run-1", Event("run-1", RunEventKind.ContainerStarted, "t1", new string('d', 5000)));
        records.AppendEvent("run-1", RunStatus.Of("run-1", RunState.Completed, RunEventKind.RunFinished) with { Result = result });

        var read = records.ReadEvents("run-1")!;

        Assert.Equal([RunEventKind.Started, RunEventKind.ContainerStarted, RunEventKind.RunFinished], read.Select(e => e.Event));
        Assert.Equal(BatchRecords.MaxDetail + " [cut]".Length, read[1].Detail!.Length);
        Assert.Equal("t1", read[1].TaskId);
        Assert.All(read, e => Assert.Null(e.Result));
        Assert.DoesNotContain("\"result\"", File.ReadAllText(Path.Combine(dir.Path, "run-1", BatchRecords.EventsFile)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_batch_with_no_records_reads_as_null_so_the_caller_can_fall_back()
    {
        using var dir = new TempDir();
        var records = new BatchRecords(dir.Path);

        Assert.Null(records.ReadEvents("run-none"));
        Assert.Same(BatchTaskRecords.Empty, records.ReadTasks("run-none"));
    }

    [Fact]
    public void A_tasks_progress_is_kept_at_most_once_in_30_seconds_and_every_other_event_always()
    {
        using var dir = new TempDir();
        var clock = new ManualTimeProvider();
        var records = new BatchRecords(dir.Path, clock);

        for (var i = 0; i < 6; i++)
        {
            records.AppendEvent("run-1", Event("run-1", RunEventKind.SessionProgress, "t1", $"tick {i}", tokens: 100 * (i + 1)));
            records.AppendEvent("run-1", Event("run-1", RunEventKind.SessionProgress, "t2", $"tick {i}", tokens: 100 * (i + 1)));
            records.AppendEvent("run-1", Event("run-1", RunEventKind.Governor, "t1", $"decision {i}"));
            clock.Advance(TimeSpan.FromSeconds(10));
        }

        var read = records.ReadEvents("run-1")!;
        // 60 seconds pass: a task's progress is kept at 0 s and at 30 s, a governor line each time.
        Assert.Equal(2, read.Count(e => e.Event == RunEventKind.SessionProgress && e.TaskId == "t1"));
        Assert.Equal(2, read.Count(e => e.Event == RunEventKind.SessionProgress && e.TaskId == "t2"));
        Assert.Equal(6, read.Count(e => e.Event == RunEventKind.Governor));
    }

    [Fact]
    public void Events_stop_at_the_cap_but_a_tasks_end_and_the_batchs_end_are_still_written()
    {
        using var dir = new TempDir();
        var records = new BatchRecords(dir.Path);
        for (var i = 0; i < BatchRecords.MaxEvents + 20; i++)
            records.AppendEvent("run-1", Event("run-1", RunEventKind.Governor, "t1", $"decision {i}"));

        records.AppendEvent("run-1", Event("run-1", RunEventKind.TaskFinished, "t1", "task t1: completed"));
        records.AppendEvent("run-1", Event("run-1", RunEventKind.RunFinished));

        var read = records.ReadEvents("run-1")!;
        Assert.Equal(BatchRecords.MaxEvents + 2, read.Count);
        Assert.Equal(RunEventKind.RunFinished, read[^1].Event);
        Assert.Equal(RunEventKind.TaskFinished, read[^2].Event);
        Assert.Equal($"decision {BatchRecords.MaxEvents - 1}", read[BatchRecords.MaxEvents - 1].Detail);
    }

    [Fact]
    public void The_cap_holds_after_a_restart_because_the_count_is_read_from_the_file()
    {
        using var dir = new TempDir();
        var first = new BatchRecords(dir.Path);
        for (var i = 0; i < BatchRecords.MaxEvents; i++)
            first.AppendEvent("run-1", Event("run-1", RunEventKind.Governor, "t1"));

        new BatchRecords(dir.Path).AppendEvent("run-1", Event("run-1", RunEventKind.Governor, "t1", "past the cap"));

        Assert.Equal(BatchRecords.MaxEvents, first.ReadEvents("run-1")!.Count);
    }

    [Fact]
    public async Task A_journaled_batchs_events_land_in_the_journal_in_order_with_gap_free_sequence_numbers_until_its_end()
    {
        using var dir = new TempDir();
        await using var journal = await DrivenJournal.OpenAsync(Path.Combine(dir.Path, "batches"));
        var records = new BatchRecords(dir.Path);
        records.Journal("run-1", journal);
        records.AppendEvent("run-1", Event("run-1", RunEventKind.Started));
        records.AppendEvent("run-2", Event("run-2", RunEventKind.Started));
        records.AppendEvent("run-1", Event("run-1", RunEventKind.ContainerStarted, "t1", new string('d', 5000)));
        records.AppendEvent("run-1", Event("run-1", RunEventKind.TaskFinished, "t1", "task t1: completed"));
        records.AppendEvent("run-1", RunStatus.Of("run-1", RunState.Completed, RunEventKind.RunFinished) with { Result = Result("run-1") });
        // An operator's note on the finished batch goes to the records only: its flow is forgotten by now.
        records.AppendEvent("run-1", Event("run-1", RunEventKind.Governor, detail: "noted"));

        var journaled = await journal.ReadEventsAsync("run-1");

        Assert.Equal([1L, 2L, 3L], journaled.Select(e => e.Sequence));
        Assert.Equal(["started", "container_started", "task_finished"], journaled.Select(e => e.Kind));
        var parsed = BatchRecords.Parse(journaled);
        Assert.Equal(records.ReadEvents("run-1")!.Take(3).Select(e => (e.Event, e.TaskId, e.Detail)), parsed.Select(p => (p.Event.Event, p.Event.TaskId, p.Event.Detail)));
        Assert.Empty(await journal.ReadEventsAsync("run-2"));
    }

    [Fact]
    public async Task The_journal_keeps_a_tasks_progress_as_the_records_do_at_most_once_in_30_seconds()
    {
        using var dir = new TempDir();
        var clock = new ManualTimeProvider();
        await using var journal = await DrivenJournal.OpenAsync(Path.Combine(dir.Path, "batches"));
        var records = new BatchRecords(dir.Path, clock);
        records.Journal("run-1", journal);

        for (var i = 0; i < 6; i++)
        {
            records.AppendEvent("run-1", Event("run-1", RunEventKind.SessionProgress, "t1", $"tick {i}", tokens: 100 * (i + 1)));
            records.AppendEvent("run-1", Event("run-1", RunEventKind.Governor, "t1", $"decision {i}"));
            clock.Advance(TimeSpan.FromSeconds(10));
        }

        var journaled = BatchRecords.Parse(await journal.ReadEventsAsync("run-1"));
        Assert.Equal(["tick 0", "tick 3"], journaled.Where(e => e.Event.Event == RunEventKind.SessionProgress).Select(e => e.Event.Detail));
        Assert.Equal(6, journaled.Count(e => e.Event.Event == RunEventKind.Governor));
        Assert.Equal(Enumerable.Range(1, 8).Select(n => (long)n), journaled.Select(e => e.Sequence));
    }

    private static readonly DateTimeOffset At = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_batch_id_that_is_not_a_plain_name_writes_and_reads_nothing_outside_the_records_directory()
    {
        using var dir = new TempDir();
        var records = new BatchRecords(Path.Combine(dir.Path, "records"));

        records.AppendEvent("../escape", Event("run-1", RunEventKind.Started));
        records.AppendEvent("a/b", Event("run-1", RunEventKind.Started));
        records.Append("..", new SessionRecord("t1", "r1", "first", null, At, At, "completed", null, 0, 0, 0, 0, 0, null, null, 0, 0));

        Assert.False(Directory.Exists(Path.Combine(dir.Path, "records")));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "escape")));
        Assert.Null(records.ReadEvents("../escape"));
    }

    [Fact]
    public void A_last_line_with_no_newline_is_still_being_written_and_is_not_read()
    {
        using var dir = new TempDir();
        dir.Write("run-1/events.jsonl", "{\"contract_version\":\"run-status/v1\",\"run_id\":\"run-1\",\"status\":\"running\",\"event\":\"started\"}\n{\"contract_version\":\"run-sta");
        var records = new BatchRecords(dir.Path);

        Assert.Equal([RunEventKind.Started], records.ReadEvents("run-1")!.Select(e => e.Event));
    }

    [Fact]
    public void An_event_line_is_the_bytes_the_contract_options_write()
    {
        using var dir = new TempDir();
        var records = new BatchRecords(dir.Path);
        var e = RunStatus.Of("run-1", RunState.Running, RunEventKind.TaskFinished) with
        {
            TaskId = "t1",
            Detail = "task t1: completed \"quoted\" <tag> é",
            Branch = "chargehand/run-s1",
            PrUrl = "https://example.test/o/r/pull/1",
            Tokens = 1200,
            Usd = 0.25m,
            Runner = "box",
        };

        records.AppendEvent("run-1", e);

        var line = File.ReadAllText(Path.Combine(dir.Path, "run-1", BatchRecords.EventsFile)).TrimEnd('\n');
        Assert.Equal(JsonSerializer.Serialize(e, ContractJson.Options), line);
        Assert.Equal(e, Assert.Single(records.ReadEvents("run-1")!));
    }

    private static void Seed(TempDir dir, string batch, DateTime written)
    {
        var file = dir.Write($"{batch}/events.jsonl", "{}\n");
        File.SetLastWriteTimeUtc(file, written);
    }

    [Fact]
    public void Prune_removes_the_oldest_batches_past_the_count()
    {
        using var dir = new TempDir();
        var clock = new ManualTimeProvider();
        var now = clock.GetUtcNow().UtcDateTime;
        for (var i = 0; i < BatchRecords.MaxBatches; i++)
            Seed(dir, $"run-{i:D4}", now.AddMinutes(-i - 1));
        Seed(dir, "run-oldest", now.AddMinutes(-BatchRecords.MaxBatches - 5));
        Seed(dir, "run-older", now.AddMinutes(-BatchRecords.MaxBatches - 6));

        var removed = new BatchRecords(dir.Path, clock).Prune(new HashSet<string>());

        Assert.Equal(["run-older", "run-oldest"], removed.Order());
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "run-0000")));
        Assert.Equal(BatchRecords.MaxBatches, new DirectoryInfo(dir.Path).GetDirectories().Length);
    }

    [Fact]
    public void Prune_removes_a_batch_idle_past_the_age_and_never_one_that_is_running()
    {
        using var dir = new TempDir();
        var clock = new ManualTimeProvider();
        var now = clock.GetUtcNow().UtcDateTime;
        Seed(dir, "run-fresh", now.AddMinutes(-5));
        Seed(dir, "run-stale", now.AddDays(-200));
        Seed(dir, "run-live-but-old", now.AddDays(-400));

        var removed = new BatchRecords(dir.Path, clock).Prune(new HashSet<string> { "run-live-but-old" });

        Assert.Equal(["run-stale"], removed);
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "run-live-but-old")));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "run-fresh")));
        Assert.Null(new BatchRecords(dir.Path, clock).ReadEvents("run-stale"));
    }
}
