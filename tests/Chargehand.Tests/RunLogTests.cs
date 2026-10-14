using Chargehand.Contracts;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;

namespace Chargehand.Tests;

/// <summary>The run log as the run store the CLI and the server share (ADR 0018).</summary>
public class RunLogTests
{
    private static StartRecord Start(string id, int pid, string? bootId = null) =>
        new(id, DateTimeOffset.UtcNow, new string('0', 32), new RunRequest("request/v1", "t", new RequestContext(false, "cheap")), pid, BootId: bootId);

    [Fact]
    public async Task A_start_without_a_run_record_is_unfinished_and_names_its_process()
    {
        using var dir = new TempDir();
        var log = new JsonlRunLog(System.IO.Path.Combine(dir.Path, "log.jsonl"));
        await log.AppendAsync(Start("run-a", Environment.ProcessId, ProcessBoot.Id), CancellationToken.None);
        await log.AppendAsync(Start("run-b", int.MaxValue, "other-boot"), CancellationToken.None);

        var a = await log.ReadAsync("run-a", CancellationToken.None);
        Assert.Null(a.Run);
        Assert.Equal("t", a.Start!.Request.Text);
        Assert.True(a.Start.OwnerAlive());
        Assert.False((await log.ReadAsync("run-b", CancellationToken.None)).Start!.OwnerAlive());
    }

    [Fact]
    public async Task A_start_from_an_earlier_boot_with_this_pid_or_no_boot_id_is_lost_in_the_list_and_one_from_this_boot_is_running()
    {
        using var dir = new TempDir();
        var log = new JsonlRunLog(System.IO.Path.Combine(dir.Path, "log.jsonl"));
        // A restarted container's server is pid 1 again.
        await log.AppendAsync(Start("run-old", Environment.ProcessId, "earlier-boot"), CancellationToken.None);
        await log.AppendAsync(Start("run-legacy", Environment.ProcessId), CancellationToken.None);
        await log.AppendAsync(Start("run-now", Environment.ProcessId, ProcessBoot.Id), CancellationToken.None);

        var states = (await log.ListAsync(new RunListQuery(), CancellationToken.None)).ToDictionary(r => r.RunId, r => r.Status);
        Assert.Equal(RunState.Lost, states["run-old"]);
        Assert.Equal(RunState.Lost, states["run-legacy"]);
        Assert.Equal(RunState.Running, states["run-now"]);
    }

    [Fact]
    public async Task A_resumed_start_names_the_new_owner_in_the_list()
    {
        using var dir = new TempDir();
        var log = new JsonlRunLog(System.IO.Path.Combine(dir.Path, "log.jsonl"));
        await log.AppendAsync(Start("run-batch", int.MaxValue, "earlier-boot"), CancellationToken.None);
        await log.AppendAsync(Start("run-batch", Environment.ProcessId, ProcessBoot.Id), CancellationToken.None);

        Assert.Equal(RunState.Running, Assert.Single(await log.ListAsync(new RunListQuery(), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_line_still_being_written_is_skipped()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "log.jsonl");
        var log = new JsonlRunLog(path);
        await log.AppendAsync(Start("run-a", Environment.ProcessId), CancellationToken.None);
        await File.AppendAllTextAsync(path, """{"run_id":"run-a","type":"ru""");

        Assert.NotNull((await log.ReadAsync("run-a", CancellationToken.None)).Start);
    }

    [Fact]
    public async Task Writers_on_one_file_keep_every_line_whole()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "log.jsonl");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            var log = new JsonlRunLog(path);
            for (var i = 0; i < 25; i++)
                await log.AppendAsync(new ScoreRecord($"run-{w}-{i}", "quality", 0.5, DateTimeOffset.UtcNow, "test"), CancellationToken.None);
        })));

        var all = await new JsonlRunLog(path).ReadAllAsync(CancellationToken.None);
        Assert.Equal(200, all.Scores.Select(s => s.RunId).Distinct().Count());
    }

    [Fact]
    public async Task An_extensions_report_round_trips_and_prints_one_line_per_source()
    {
        using var dir = new TempDir();
        var log = new JsonlRunLog(System.IO.Path.Combine(dir.Path, "log.jsonl"));
        var chain = new PromptChain([], new AsSent("v", "build", "m", "2026-09-29"));
        var result = new ResultContract("result/v1", "run-x", "n1", new string('0', 32), chain, ResultStatus.Completed, "s", [], [], [], [], 0.5, new Usage(0, 0, 0, 0, 0));
        var report = new ExtensionsReport([new MemoryReport("notes", 2, null, 1, null), new MemoryReport("broken", 0, "timed out after 10 s", 0, "not retained")], []);
        await log.AppendAsync(new RunRecord("run-x", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "cheap", "answer", null, result, "answer", report), CancellationToken.None);

        var stored = (await log.ReadAsync("run-x", CancellationToken.None)).Run!.Extensions!;

        Assert.Equal(2, stored.Memory[0].Recalled);
        Assert.Equal(["memory notes: recalled 2, retained 1", "memory broken: recall skipped (timed out after 10 s), retain skipped (not retained)"], stored.Lines());
    }

    [Fact]
    public void An_extensions_report_prints_one_line_per_service_with_what_was_dropped()
    {
        var report = new ExtensionsReport([], [
            new ServiceReport("team-docs", ["read_doc", "search_docs"], []),
            new ServiceReport("wiki", ["get_page"], ["tool_missing: nope_*"]),
            new ServiceReport("gone", [], ["unreachable: connection refused"])]);

        Assert.Equal(["service team-docs: granted read_doc, search_docs", "service wiki: granted get_page (tool_missing: nope_*)", "service gone: dropped (unreachable: connection refused)"],
            report.Lines());
    }

    [Fact]
    public async Task A_run_record_written_before_the_report_existed_still_reads()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "log.jsonl");
        var chain = new PromptChain([], new AsSent("v", "build", "m", "2026-09-29"));
        var result = new ResultContract("result/v1", "run-y", "n1", new string('0', 32), chain, ResultStatus.Completed, "s", [], [], [], [], 0.5, new Usage(0, 0, 0, 0, 0));
        var log = new JsonlRunLog(path);
        await log.AppendAsync(new RunRecord("run-y", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "cheap", "answer", null, result), CancellationToken.None);

        Assert.Null((await log.ReadAsync("run-y", CancellationToken.None)).Run!.Extensions);
    }
}
