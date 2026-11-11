using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Oversight.Tests;

/// <summary>Issue #781: <c>chargehand learn phases</c> gives the median wall time per run phase over the last runs that measured them. The entries are synthetic.</summary>
public class LearningPhasesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static LearningEntry Entry(int minutesAgo, LearningPhases? phases) =>
        new(new LearningRecord("batch", $"task-{minutesAgo}", $"run-{minutesAgo}", Now.AddMinutes(-minutesAgo), "o/r", "M", "caller", new LearningBudget(null, new BudgetSource("none")),
            new LearningSpend(1m, false, 10, 5, 0, 0, 3, 0), LearningOutcome.Completed, "completed", null, [], false, Phases: phases), null);

    [Fact]
    public void The_last_runs_with_phase_times_give_a_median_per_phase_slowest_first()
    {
        // The oldest run is past the last two and is left out; a record without phases is skipped.
        List<LearningEntry> entries = [Entry(30, new(999, 999, 999, 999, 999)), Entry(20, new(4, 10, 600, 120, 30)), Entry(15, null), Entry(10, new(6, 20, 400, 300, 50))];
        var table = LearningReport.RenderPhases(entries, last: 2);
        var rows = table.Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal)).Skip(1).ToList();
        Assert.Equal(["| model turns | 2 | 400.0 | 600.0 |", "| checks | 2 | 120.0 | 300.0 |", "| handover | 2 | 30.0 | 50.0 |", "| restore | 2 | 10.0 | 20.0 |", "| clone | 2 | 4.0 | 6.0 |"], rows);
        Assert.Contains("The last 2 runs", table);
    }

    [Fact]
    public async Task The_cli_refuses_a_run_count_that_is_not_a_positive_number()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(2, await LearningCli.PhasesAsync(["--last", "0"], new JsonlLearningStore(Path.Combine(Path.GetTempPath(), $"phases-{Guid.NewGuid():N}.jsonl")), output, error, default));
        Assert.Contains("usage: chargehand learn phases", error.ToString());
    }
}
