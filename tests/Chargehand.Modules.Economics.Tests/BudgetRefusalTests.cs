using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Economics.Api;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Oversight.Api;
using static Chargehand.Modules.Economics.Tests.Fixtures;

namespace Chargehand.Modules.Economics.Tests;

public class BudgetRefusalTests
{
    private static LearningEntry Task(string run, string stopReason, int daysAgo, string repository = "o/r", string? prOutcome = null)
    {
        var pr = prOutcome is null ? null : new LearningPr($"https://github.com/{repository}/pull/1", 1, new RemoteRepository("o", "r"), null);
        var record = new LearningRecord("batch", $"task-{run}", run, Now.AddDays(-daysAgo), repository, "M", "caller", new LearningBudget(null, new BudgetSource("none")),
            new LearningSpend(1m, false, 10, 5, 0, 0, 3, 0), stopReason == "completed" ? LearningOutcome.Completed : LearningOutcome.Failed, stopReason, null, [], false, pr);
        return new LearningEntry(record, prOutcome is null ? null : new PrOutcomeRecord(run, Now, prOutcome, null));
    }

    /// <summary>Three tasks a budget refusal ended and two that ended otherwise, one of them merged; one refusal is older than 7 days.</summary>
    private static IReadOnlyList<LearningEntry> Synthetic() =>
    [
        Task("a", BudgetRefusals.StopReason, 1),
        Task("b", BudgetRefusals.StopReason, 2, repository: "o/s"),
        Task("c", BudgetRefusals.StopReason, 10),
        Task("d", "completed", 1, prOutcome: PrOutcomes.MergedUnchanged),
        Task("e", "turn_limit", 3),
    ];

    [Fact]
    public void Tasks_a_budget_refusal_ended_are_counted_per_repository_over_7_and_30_days()
    {
        var windows = BudgetRefusals.Build(Synthetic(), Now);

        Assert.Equal([7, 30], windows.Select(w => w.Days));
        Assert.Equal(new BudgetRefusalRow("*", 4, 2, 1), windows[0].Total);
        Assert.Equal(new BudgetRefusalRow("*", 5, 3, 1), windows[1].Total);
        Assert.Equal([new BudgetRefusalRow("o/r", 4, 2, 1), new BudgetRefusalRow("o/s", 1, 1, 0)], windows[1].Repositories);
    }

    [Fact]
    public async Task Cost_json_reports_the_refusals_and_the_merged_pull_requests()
    {
        var output = new StringWriter();
        var exit = await CostCli.RunAsync(["--json"], new MemoryLog(Log()), new MemoryLearning(Synthetic()), output, new StringWriter(), Now,
            report => JsonSerializer.Serialize(report, ContractJson.Options), default);

        Assert.Equal(0, exit);
        var windows = JsonDocument.Parse(output.ToString()).RootElement.GetProperty("budget_refusals");
        var month = windows[1].GetProperty("total");
        Assert.Equal((30, 3, 1), (windows[1].GetProperty("days").GetInt32(), month.GetProperty("budget_refused_tasks").GetInt32(), month.GetProperty("merged_prs").GetInt32()));
        Assert.Equal(2, windows[0].GetProperty("total").GetProperty("budget_refused_tasks").GetInt32());
    }

    [Fact]
    public async Task The_cost_table_prints_the_refusals()
    {
        var output = new StringWriter();
        await CostCli.RunAsync([], new MemoryLog(Log()), new MemoryLearning(Synthetic()), output, new StringWriter(), Now, _ => "", default);

        Assert.Contains("repository | tasks | ended by a budget refusal | merged prs", output.ToString());
        Assert.Contains("* | 5 | 3 | 1", output.ToString());
    }

    private sealed class MemoryLog(RunLogData data) : IRunLog
    {
        public Task AppendAsync(StartRecord record, CancellationToken ct) => throw new NotSupportedException();

        public Task AppendAsync(CallRecord record, CancellationToken ct) => throw new NotSupportedException();

        public Task AppendAsync(RunRecord record, CancellationToken ct) => throw new NotSupportedException();

        public Task<RunEntry> ReadAsync(string runId, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<RunSummary>> ListAsync(RunListQuery query, CancellationToken ct) => throw new NotSupportedException();

        public Task<RunLogData> ReadAllAsync(CancellationToken ct) => System.Threading.Tasks.Task.FromResult(data);
    }

    private sealed class MemoryLearning(IReadOnlyList<LearningEntry> entries) : ILearningStore
    {
        public Task AppendAsync(LearningRecord record, CancellationToken ct) => throw new NotSupportedException();

        public Task AppendAsync(PrOutcomeRecord record, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<LearningEntry>> ReadAsync(CancellationToken ct, bool includeBench = false) => System.Threading.Tasks.Task.FromResult(entries);
    }
}
