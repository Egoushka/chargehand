using Chargehand.Contracts;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>CHARGEHAND-214: what the console reads about a batch and the run log does not keep, one directory per batch beside the run log: its events, and a line per session, governor log,
/// check snapshot and CI fix attempt of its tasks. Append-only, bounded, and pruned with the oldest batches.</summary>
public class BatchRecordsTests
{
    private static RunStatus Event(string batch, RunEventKind kind, string? task = null, string? detail = null, long? tokens = null) =>
        RunStatus.Of(batch, RunState.Running, kind) with { TaskId = task, Detail = detail, Tokens = tokens };

    [Fact]
    public void Events_are_read_back_in_order_without_the_result_and_with_a_long_detail_cut()
    {
        using var dir = new TempDir();
        var records = new BatchRecords(dir.Path);
        var result = ConsoleSeed.Result("run-1", ResultStatus.Completed, "done");
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

    private static readonly DateTimeOffset At = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static SessionRecord Session(string task, string run, string role = "first", string status = "completed", string? reason = null) =>
        new(task, run, role, "mac", At, At.AddMinutes(9), status, reason, 31, 1000, 200, 3000, 400, 1.5m, null, 0, 2);

    [Fact]
    public void Sessions_governor_logs_checks_and_attempts_round_trip_per_task()
    {
        using var dir = new TempDir();
        var records = new BatchRecords(dir.Path);
        var decision = new GovernorDecision(At, 12, Chargehand.Modules.Oversight.Api.GovernorMode.Observe, GovernorAction.Checkpoint, "spend", "spend_checkpoint", new Dictionary<string, string> { ["spend"] = "3.4" }, 3.4m, 4m, "M", false,
            new BudgetSource("history", 22, 80, "repository"));

        records.Append("run-1", Session("t1", "r1", reason: null));
        records.Append("run-1", Session("t1", "r2", "continuation", "stalled", "turn_limit"));
        records.Append("run-1", new GovernorRecord("t1", "r1", 1, [decision]));
        records.Append("run-1", new ChecksRecord("t1", At, 7, "abc", "BLOCKED", false, true, "wait", [new CheckRecord("build", true, "pending", 900)]));
        records.Append("run-1", new CiAttemptRecord("t1", 1, 3, At, "abcdef012345", ["build"], true, "tests passed"));
        records.Append("run-2", Session("t9", "r9"));

        var read = records.ReadTasks("run-1");

        Assert.Equal(["r1", "r2"], read.Sessions.Select(s => s.RunId));
        Assert.Equal(("continuation", "stalled", "turn_limit", "mac"), (read.Sessions[1].Role, read.Sessions[1].Status, read.Sessions[1].Reason, read.Sessions[1].Runner));
        var governor = Assert.Single(read.Governor);
        Assert.Equal((GovernorAction.Checkpoint, "spend", 3.4m, "history"), (governor.Decisions[0].Action, governor.Decisions[0].Signal, governor.Decisions[0].SpendUsd, governor.Decisions[0].BudgetSource!.Kind));
        Assert.Equal(("build", true, "pending", 900L), (read.Checks[0].Checks[0].Name, read.Checks[0].Checks[0].Required, read.Checks[0].Checks[0].Outcome, read.Checks[0].Checks[0].JobId));
        Assert.Equal((1, true), (read.Attempts[0].Attempt, read.Attempts[0].Pushed));
        Assert.Equal(["build"], read.Attempts[0].Failing);
        Assert.Equal(["r9"], records.ReadTasks("run-2").Sessions.Select(s => s.RunId));
    }

    [Fact]
    public void Check_snapshots_and_attempts_are_capped_per_task_and_one_task_does_not_use_up_anothers()
    {
        using var dir = new TempDir();
        var records = new BatchRecords(dir.Path);
        for (var i = 0; i < BatchRecords.MaxChecksPerTask + 5; i++)
        {
            records.Append("run-1", new ChecksRecord("t1", At.AddMinutes(i), 7, $"h{i}", "BLOCKED", false, false, "wait", []));
            records.Append("run-1", new CiAttemptRecord("t1", i, 3, At, "k", [], false, "x"));
        }
        records.Append("run-1", new ChecksRecord("t2", At, 8, "h", "CLEAN", false, false, "done", []));

        var read = new BatchRecords(dir.Path).ReadTasks("run-1");

        Assert.Equal(BatchRecords.MaxChecksPerTask, read.Checks.Count(c => c.TaskId == "t1"));
        Assert.Equal(BatchRecords.MaxChecksPerTask, read.Attempts.Count(c => c.TaskId == "t1"));
        Assert.Single(read.Checks, c => c.TaskId == "t2");
    }

    [Fact]
    public void A_batch_id_that_is_not_a_plain_name_writes_and_reads_nothing_outside_the_records_directory()
    {
        using var dir = new TempDir();
        var records = new BatchRecords(Path.Combine(dir.Path, "records"));

        records.AppendEvent("../escape", Event("run-1", RunEventKind.Started));
        records.AppendEvent("a/b", Event("run-1", RunEventKind.Started));
        records.Append("..", Session("t1", "r1"));

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
