using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Tests;

/// <summary>One line per task attempt in <c>learning.jsonl</c>, and benchmark tasks kept out of what production reads (ADR 0045): the store writes an attempt it already holds once, a reader takes one entry
/// per (batch, task, run) from a file that holds it twice, and a record from a benchmark batch is tagged and left out of budgets, reports and pull request read-back unless asked for.</summary>
public class LearningDedupTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private sealed class Stores : IDisposable
    {
        public TempDir Dir { get; } = new();

        public string Path => System.IO.Path.Combine(Dir.Path, "learning.jsonl");

        public JsonlLearningStore Store => new(Path);

        public string[] Lines => File.Exists(Path) ? File.ReadAllLines(Path) : [];

        public void Dispose() => Dir.Dispose();
    }

    private static DrivenIntegrator MergeRepository() => new(1, 2, "app-key", new Dictionary<string, string> { ["o/r"] = "merge" }, null);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static LearningRecord Bench(string run) => LearningTests.Record(run) with { Bench = true };

    [Fact]
    public async Task A_record_the_file_already_holds_is_not_written_again_even_by_a_new_process_and_at_another_time()
    {
        using var stores = new Stores();
        await stores.Store.AppendAsync(LearningTests.Record("r1"), default);

        await stores.Store.AppendAsync(LearningTests.Record("r1", at: Now), default);
        await new JsonlLearningStore(stores.Path).AppendAsync(LearningTests.Record("r1", at: Now.AddSeconds(2)), default);

        Assert.Single(stores.Lines);
    }

    [Fact]
    public async Task A_record_that_says_something_new_about_the_attempt_supersedes_the_old_one_and_the_old_one_is_not_written_back()
    {
        using var stores = new Stores();
        await stores.Store.AppendAsync(LearningTests.Record("r1", usd: 1m), default);
        await stores.Store.AppendAsync(LearningTests.Record("r1", usd: 1.4m), default);
        await stores.Store.AppendAsync(LearningTests.Record("r1", usd: 1.4m), default);

        Assert.Equal(2, stores.Lines.Length);
        Assert.Equal(1.4m, Assert.Single(await stores.Store.ReadAsync(default)).Record.Spend.UsdEstimate);
    }

    [Fact]
    public async Task Two_tasks_with_one_run_id_in_different_batches_are_two_attempts()
    {
        using var stores = new Stores();
        await stores.Store.AppendAsync(LearningTests.Record("r1"), default);
        await stores.Store.AppendAsync(LearningTests.Record("r1") with { BatchId = "run-another" }, default);

        Assert.Equal(2, stores.Lines.Length);
        Assert.Equal(2, (await stores.Store.ReadAsync(default)).Count);
    }

    [Fact]
    public async Task A_file_that_holds_an_attempt_twice_reads_once_and_counts_its_spend_once()
    {
        using var stores = new Stores();
        await stores.Store.AppendAsync(LearningTests.Record("r1", usd: 2m), default);
        await stores.Store.AppendAsync(LearningTests.Record("r2", usd: 3m), default);
        var written = stores.Lines;
        File.AppendAllLines(stores.Path, written.Select(l => l.Replace("\"at\":\"2026-10-13T11:00:00+00:00\"", "\"at\":\"2026-10-13T11:00:02+00:00\"", StringComparison.Ordinal)));
        Assert.Equal(4, stores.Lines.Length);

        var entries = await stores.Store.ReadAsync(default);
        var report = LearningReport.Build(entries);

        Assert.Equal(["r1", "r2"], entries.Select(e => e.Record.RunId));
        Assert.Equal(2, report.Tasks);
        Assert.Equal(3m, Assert.Single(report.Groups).SpendP80Usd);
        Assert.Equal(2, Assert.Single(report.Groups).SpendKnown);
    }

    [Fact]
    public async Task A_follow_up_that_changes_nothing_leaves_one_line_and_one_that_spends_more_leaves_the_early_line_and_the_final_one()
    {
        using var stores = new Stores();
        var host = new FakeIntegrationHost();
        using var rig = new DrivenRunTests.Rig(learning: stores.Store, integrator: MergeRepository(), integration: host, outcomes: (_, _) => new SessionOutcome(SessionStatus.Completed, "", 30, 1000, 200, 4000, null, 0, [], true, CacheWriteTokens: 300, Commits: 4, UsdEstimate: 1m));
        host.Script = h => h.Current with { Merged = true };

        await rig.Run.RunAsync(RunRequests.One(), "run-batch-d", g => $"tok-{g.RunId}", default);

        Assert.Single(stores.Lines);
    }

    [Fact]
    public async Task A_benchmark_record_is_tagged_in_the_file_and_an_ordinary_one_is_not()
    {
        using var stores = new Stores();
        await stores.Store.AppendAsync(Bench("b1"), default);
        await stores.Store.AppendAsync(LearningTests.Record("r1"), default);

        Assert.Contains("\"bench\":true", stores.Lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("bench", stores.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Benchmark_records_are_read_only_when_asked_for_and_old_files_without_the_tag_still_read()
    {
        using var stores = new Stores();
        await stores.Store.AppendAsync(Bench("b1"), default);
        await stores.Store.AppendAsync(LearningTests.Record("r1"), default);

        Assert.Equal(["r1"], (await stores.Store.ReadAsync(default)).Select(e => e.Record.RunId));
        Assert.Equal(["b1", "r1"], (await stores.Store.ReadAsync(default, includeBench: true)).Select(e => e.Record.RunId).Order());
        Assert.All(await stores.Store.ReadAsync(default), e => Assert.False(e.Record.Bench));
    }

    [Fact]
    public async Task Benchmark_tasks_do_not_move_the_report_the_budget_or_the_pull_request_read_back()
    {
        using var stores = new Stores();
        var pr = new LearningPr("https://example.test/o/r/pull/7", 7, new RemoteRepository("o", "r"), "aaaa");
        await stores.Store.AppendAsync(LearningTests.Record("r1", usd: 2m), default);
        await stores.Store.AppendAsync(Bench("b1") with { Spend = LearningTests.Record("b1", usd: 50m).Spend, Pr = pr }, default);

        var report = LearningReport.Build(await stores.Store.ReadAsync(default));
        var host = new FakeIntegrationHost { Current = FakeIntegrationHost.Snapshot(merged: true) };
        var written = await new PrOutcomeTracker(stores.Store, _ => host, new FixedClock(Now)).RefreshAsync(14, default);

        Assert.Equal(1, report.Tasks);
        Assert.Equal(2m, Assert.Single(report.Groups).SpendP80Usd);
        Assert.Equal(0, written);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task The_learn_report_command_counts_benchmark_tasks_only_with_include_bench()
    {
        using var stores = new Stores();
        await stores.Store.AppendAsync(LearningTests.Record("r1"), default);
        await stores.Store.AppendAsync(Bench("b1") with { Repository = "local/bench" }, default);
        StringWriter plain = new(), withBench = new(), error = new();

        Assert.Equal(0, await LearningCli.RunAsync([], stores.Store, plain, error, Now, default));
        Assert.Equal(0, await LearningCli.RunAsync(["--include-bench"], stores.Store, withBench, error, Now, default));

        Assert.DoesNotContain("local/bench", plain.ToString(), StringComparison.Ordinal);
        Assert.Contains("local/bench", withBench.ToString(), StringComparison.Ordinal);
        Assert.Contains("1 tasks", plain.ToString(), StringComparison.Ordinal);
        Assert.Contains("2 tasks", withBench.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_server_leaves_benchmark_tasks_out_of_the_report_unless_bench_is_included()
    {
        using var stores = new Stores();
        await stores.Store.AppendAsync(LearningTests.Record("r1", usd: 2m, at: DateTimeOffset.UtcNow.AddHours(-1)), default);
        await stores.Store.AppendAsync(Bench("b1") with { At = DateTimeOffset.UtcNow.AddHours(-1) }, default);
        DrivenRunTests.Rig? rig = null;
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), driven: (root, log) => (rig = new DrivenRunTests.Rig(dir: root, log: log, learning: stores.Store)).Run);
        using var _ = rig;

        Assert.Equal(1, (await s.Http.GetFromJsonAsync<JsonElement>("/v1/learn")).GetProperty("tasks").GetInt32());
        Assert.Equal(2, (await s.Http.GetFromJsonAsync<JsonElement>("/v1/learn?bench=include")).GetProperty("tasks").GetInt32());
    }

    [Fact]
    public async Task A_batch_asked_to_be_a_benchmark_tags_every_record_it_writes_and_another_does_not()
    {
        using var stores = new Stores();
        using var rig = new DrivenRunTests.Rig(learning: stores.Store, outcomes: (_, _) => new SessionOutcome(SessionStatus.Completed, "", 30, 1000, 200, 4000, null, 0, [], true, CacheWriteTokens: 300, Commits: 4, UsdEstimate: 1m));

        await rig.Run.RunAsync(RunRequests.One(bench: true), "run-batch-b", g => $"tok-{g.RunId}", default);

        Assert.Single(stores.Lines);
        Assert.True(Assert.Single(await stores.Store.ReadAsync(default, includeBench: true)).Record.Bench);
        Assert.Empty(await stores.Store.ReadAsync(default));
    }

    private static class RunRequests
    {
        public static RunRequest One(bool bench = false) =>
            new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", "abc1234def5678abc1234def5678abc1234def56")),
                Driven: new RequestDriven([new DrivenTask("t1", Goal: "one")], MaxParallel: 1, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m, Bench: bench ? true : null));
    }
}
