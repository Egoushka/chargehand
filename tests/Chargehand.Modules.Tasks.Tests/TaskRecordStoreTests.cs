using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Tasks.Tests;

/// <summary>CHARGEHAND-214: the console's records of a batch's tasks, <c>tasks.jsonl</c> in the batch's directory, a line per session, governor log, check snapshot and CI fix attempt.</summary>
public sealed class TaskRecordStoreTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("chargehand-task-records-");

    public void Dispose() => _dir.Delete(recursive: true);

    private static SessionRecord Session(string task, string run, string role = "first", string status = "completed", string? reason = null) =>
        new(task, run, role, "mac", At, At.AddMinutes(9), status, reason, 31, 1000, 200, 3000, 400, 1.5m, null, 0, 2);

    private static GovernorDecision Decision => new(At, 12, GovernorMode.Observe, GovernorAction.Checkpoint, "spend", "spend_checkpoint", new Dictionary<string, string> { ["spend"] = "3.4" }, 3.4m, 4m, "M", false,
        new BudgetSource("history", 22, 80, "repository"));

    [Fact]
    public void Sessions_governor_logs_checks_and_attempts_round_trip_per_task()
    {
        var records = new TaskRecordStore(_dir.FullName);

        records.Append("run-1", Session("t1", "r1", reason: null));
        records.Append("run-1", Session("t1", "r2", "continuation", "stalled", "turn_limit"));
        records.Append("run-1", new GovernorRecord("t1", "r1", 1, [Decision]));
        records.Append("run-1", new ChecksRecord("t1", At, 7, "abc", "BLOCKED", false, true, "wait", [new CheckRecord("build", true, "pending", 900)]));
        records.Append("run-1", new CiAttemptRecord("t1", 1, 3, At, "abcdef012345", ["build"], true, "tests passed"));
        records.Append("run-2", Session("t9", "r9"));

        var read = records.Read("run-1");

        Assert.Equal(["r1", "r2"], read.Sessions.Select(s => s.RunId));
        Assert.Equal(("continuation", "stalled", "turn_limit", "mac"), (read.Sessions[1].Role, read.Sessions[1].Status, read.Sessions[1].Reason, read.Sessions[1].Runner));
        var governor = Assert.Single(read.Governor);
        Assert.Equal((GovernorAction.Checkpoint, "spend", 3.4m, "history"), (governor.Decisions[0].Action, governor.Decisions[0].Signal, governor.Decisions[0].SpendUsd, governor.Decisions[0].BudgetSource!.Kind));
        Assert.Equal(("build", true, "pending", 900L), (read.Checks[0].Checks[0].Name, read.Checks[0].Checks[0].Required, read.Checks[0].Checks[0].Outcome, read.Checks[0].Checks[0].JobId));
        Assert.Equal((1, true), (read.Attempts[0].Attempt, read.Attempts[0].Pushed));
        Assert.Equal(["build"], read.Attempts[0].Failing);
        Assert.Equal(["r9"], records.Read("run-2").Sessions.Select(s => s.RunId));
    }

    [Fact]
    public void Check_snapshots_and_attempts_are_capped_per_task_and_one_task_does_not_use_up_anothers()
    {
        var records = new TaskRecordStore(_dir.FullName);
        for (var i = 0; i < TaskRecordStore.MaxChecksPerTask + 5; i++)
        {
            records.Append("run-1", new ChecksRecord("t1", At.AddMinutes(i), 7, $"h{i}", "BLOCKED", false, false, "wait", []));
            records.Append("run-1", new CiAttemptRecord("t1", i, 3, At, "k", [], false, "x"));
        }
        records.Append("run-1", new ChecksRecord("t2", At, 8, "h", "CLEAN", false, false, "done", []));

        var read = new TaskRecordStore(_dir.FullName).Read("run-1");

        Assert.Equal(TaskRecordStore.MaxChecksPerTask, read.Checks.Count(c => c.TaskId == "t1"));
        Assert.Equal(TaskRecordStore.MaxChecksPerTask, read.Attempts.Count(c => c.TaskId == "t1"));
        Assert.Single(read.Checks, c => c.TaskId == "t2");
    }

    [Fact]
    public void A_session_and_its_governor_log_are_kept_once_per_run_id()
    {
        var records = new TaskRecordStore(_dir.FullName);

        records.Append("run-1", Session("t1", "r1"));
        records.Append("run-1", new GovernorRecord("t1", "r1", 1, [Decision]));
        records.Append("run-1", Session("t1", "r1", status: "failed"));
        records.Append("run-1", new GovernorRecord("t1", "r1", 2, [Decision, Decision]));

        var read = new TaskRecordStore(_dir.FullName).Read("run-1");
        Assert.Equal("completed", Assert.Single(read.Sessions).Status);
        Assert.Equal(1, Assert.Single(read.Governor).Total);
    }

    /// <summary>The file outlives a deploy: a line is written byte for byte as the contract options wrote it before the store had source-generated JSON, so the old lines read back too.</summary>
    [Fact]
    public void Lines_are_written_as_the_contract_options_wrote_them()
    {
        var records = new TaskRecordStore(_dir.FullName);
        var session = Session("t1", "r1", "continuation", "stalled", "turn_limit");
        var governor = new GovernorRecord("t1", "r1", 1, [Decision]);
        var checks = new ChecksRecord("t1", At, 7, "abc", "BLOCKED", false, true, "wait", [new CheckRecord("build", true, "pending", null)]);
        var attempt = new CiAttemptRecord("t1", 1, 3, At, "abcdef012345", ["build"], true, new string('d', TaskRecordStore.MaxDetail + 10));

        records.Append("run-1", session);
        records.Append("run-1", governor);
        records.Append("run-1", checks);
        records.Append("run-1", attempt);

        string Before<T>(T record, string type)
        {
            var node = JsonSerializer.SerializeToNode(record, ContractJson.Options)!;
            node["type"] = type;
            return node.ToJsonString();
        }
        Assert.Equal([Before(session, "session"), Before(governor, "governor"), Before(checks, "checks"),
            Before(attempt with { Detail = attempt.Detail[..TaskRecordStore.MaxDetail] + " [cut]" }, "ci_attempt")],
            File.ReadAllLines(Path.Combine(_dir.FullName, "run-1", TaskRecordStore.TasksFile)));
    }

    [Fact]
    public void A_batch_id_that_is_not_a_plain_name_writes_and_reads_nothing()
    {
        var root = Path.Combine(_dir.FullName, "records");
        var records = new TaskRecordStore(root);

        records.Append("..", Session("t1", "r1"));
        records.Append("a/b", Session("t1", "r1"));

        Assert.False(Directory.Exists(root));
        Assert.Same(BatchTaskRecords.Empty, records.Read(".."));
    }
}
