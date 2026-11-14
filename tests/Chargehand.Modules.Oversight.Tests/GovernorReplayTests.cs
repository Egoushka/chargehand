using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Oversight.Tests;

/// <summary>Issue 436: a candidate governor policy replayed over recorded tasks counts stops, false stops (stopped tasks that completed) and the spend after each stop. The entries are synthetic.</summary>
public class GovernorReplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static LearningEntry Task(string id, string size, string outcome, decimal budget, decimal spent, params GovernorCount[] governor) =>
        new(new LearningRecord("batch", id, $"run-{id}", Now, "o/r", size, "caller", new LearningBudget(budget, new BudgetSource("size")),
            new LearningSpend(spent, false, 10, 5, 0, 0, 3, 0), outcome, outcome, "observe", governor, false), null);

    private static readonly List<LearningEntry> Log =
    [
        Task("a", "S", LearningOutcome.Completed, 2m, 3m, new GovernorCount("spend", "stop", 1, 0)),
        Task("b", "S", LearningOutcome.Failed, 2m, 5m, new GovernorCount("spend", "stop", 1, 0)),
        Task("c", "M", LearningOutcome.Completed, 4m, 6m, new GovernorCount("spend", "stop", 1, 0)),
        Task("d", "M", LearningOutcome.Completed, 4m, 3m),
        Task("e", "L", LearningOutcome.Failed, 8m, 4m, new GovernorCount("repeated_call", "checkpoint", 2, 0), new GovernorCount("environment_probing", "warn", 1, 0)),
    ];

    [Fact]
    public void The_recorded_budgets_count_stops_false_stops_and_spend_after_the_stop_per_size()
    {
        var report = GovernorReplay.Build(Log, new ReplayPolicy());

        Assert.Equal(5, report.Tasks);
        Assert.Equal(new ReplayRow("all", 3, 2, 1m + 3m + 2m), report.Total);
        Assert.Equal([new ReplayRow("S", 2, 1, 4m), new ReplayRow("M", 1, 1, 2m)], report.BySize);
        Assert.Equal([new ReplayRow("spend", 3, 2, 6m)], report.BySignal);
        Assert.Equal(2d / 3, report.Total.FalseStopRate);
    }

    [Fact]
    public void A_larger_budget_factor_spares_the_tasks_that_finished_within_it()
    {
        var report = GovernorReplay.Build(Log, new ReplayPolicy(BudgetFactor: 1.5m));

        // a (3 of 3.0) and c (6 of 6.0) are no longer over; only b is.
        Assert.Equal(new ReplayRow("all", 1, 0, 2m), report.Total);
        Assert.Equal(0d, report.Total.FalseStopRate);
    }

    [Fact]
    public void A_signal_stops_a_task_only_when_the_policy_names_it_and_the_log_shows_a_checkpoint_or_stop()
    {
        var named = GovernorReplay.Build(Log, new ReplayPolicy(StopSignals: ["repeated_call", "environment_probing"]));

        Assert.Equal(4, named.Total.Stops);
        Assert.Equal(new ReplayRow("repeated_call", 1, 0, 0m), named.BySignal.Single(r => r.Key == "repeated_call"));
        Assert.DoesNotContain(named.BySignal, r => r.Key == "environment_probing");
        Assert.DoesNotContain(GovernorReplay.Build(Log, new ReplayPolicy()).BySignal, r => r.Key == "repeated_call");
    }

    [Fact]
    public void A_task_two_signals_stop_counts_once_in_the_total()
    {
        var both = Task("f", "S", LearningOutcome.Completed, 1m, 2m, new GovernorCount("repeated_call", "checkpoint", 1, 0));

        var report = GovernorReplay.Build([both], new ReplayPolicy(StopSignals: ["repeated_call"]));

        Assert.Equal(1, report.Total.Stops);
        Assert.Equal(2, report.BySignal.Count);
    }

    [Fact]
    public void The_since_time_leaves_out_older_tasks()
    {
        Assert.Equal(0, GovernorReplay.Build(Log, new ReplayPolicy(), Now.AddDays(1)).Tasks);
    }

    [Fact]
    public async Task The_cli_prints_the_replay_and_refuses_a_bad_factor()
    {
        var path = Path.Combine(Path.GetTempPath(), $"replay-{Guid.NewGuid():N}.jsonl");
        var store = new JsonlLearningStore(path);
        foreach (var e in Log)
            await store.AppendAsync(e.Record, default);
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(0, await LearningCli.ReplayAsync(["--stop-signal", "repeated_call"], store, output, error, Now, default));
        Assert.Contains("5 tasks replayed, 4 stopped, 2 of them completed anyway (50%", output.ToString());
        Assert.Contains("| S | 2 | 1 | 50% | $4.00 |", output.ToString());
        Assert.Equal(2, await LearningCli.ReplayAsync(["--budget-factor", "0"], store, output, error, Now, default));
        Assert.Contains("usage: chargehand learn replay", error.ToString());
        File.Delete(path);
    }
}
