using Chargehand.Contracts;
using Chargehand.RunLog;
using Chargehand.Runtime;

namespace Chargehand.Tests;

/// <summary>Routing suggestions from the run log: static defaults until a cell has enough scored runs (ADR 0019).</summary>
public class RoutingReportTests
{
    private static readonly PromptChain Chain = new([], new AsSent("2.0.16", "build", "p/m", "2026-09-26"));

    /// <summary>Runs of one preset and node kind on one model, each with one call and an optional quality score.</summary>
    private static RunLogData Runs(string model, int count, decimal? usd, double? score, int offset = 0)
    {
        var start = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var ids = Enumerable.Range(offset, count).Select(i => $"run-{i}").ToList();
        return new RunLogData([],
            [.. ids.Select(id => new RunRecord(id, start, start.AddSeconds(60), "cheap", "answer", null,
                new ResultContract("result/v1", id, "worker", new string('0', 32), Chain, ResultStatus.Completed, "s", [], [], [], [], 0.8, new Usage(0, 0, 0, 0, usd))))],
            [.. ids.Select(id => new CallRecord(id, "worker", "worker", "ses", "msg", model, start, 1000, new TokenCounts(1000, 100, 0, 4000, 0), usd, Chain))],
            score is null ? [] : [.. ids.Select(id => new ScoreRecord(id, "quality", score.Value, start, "eval:t"))]);
    }

    private static RunLogData Both(RunLogData a, RunLogData b) =>
        new([], [.. a.Runs, .. b.Runs], [.. a.Calls, .. b.Calls], [.. a.Scores, .. b.Scores]);

    [Fact]
    public void A_cell_keeps_its_default_until_both_models_have_enough_scored_runs()
    {
        var report = RoutingReport.Build(Both(Runs("p/large", 7, 0.2m, 0.8), Runs("p/small", 3, 0.01m, 0.8, offset: 100)));
        Assert.True(report.Contains("| cheap | worker | p/large | 7 | 7 | 7 | 0.80 | 5000 | 80% | 0.2000 |", StringComparison.Ordinal), report);
        Assert.Contains("keep default (3 scored < 20)", report, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate:", report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cheaper_model_within_the_quality_tolerance_is_suggested_not_switched()
    {
        var report = RoutingReport.Build(Both(Runs("p/large", 25, 0.2m, 0.8), Runs("p/small", 20, 0.01m, 0.75, offset: 100)));
        Assert.Contains("| p/small | 20 | 20 | 20 | 0.75 |", report, StringComparison.Ordinal);
        Assert.Contains("candidate: score -0.05, cost -95%", report, StringComparison.Ordinal);
    }

    /// <summary>ADR 0026: an unpriced model's runs report unknown cost, not a $0 average that would read as measured.</summary>
    [Fact]
    public void Unknown_cost_runs_are_shown_as_unknown_and_never_suggested_on_price_alone()
    {
        var report = RoutingReport.Build(Both(Runs("p/large", 20, 0.2m, 0.8), Runs("p/small", 20, null, 0.8, offset: 100)));
        Assert.Contains("| p/small | 20 | 20 | 20 | 0.80 | 5000 | 80% | — | — |", report, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate:", report, StringComparison.Ordinal);
    }
}
