using Chargehand.Evals;
using Chargehand.Verification;

namespace Chargehand.Tests;

/// <summary>Goal 0.8 (ADR 0036): the labelled set and the harness that measures the support judge on it.</summary>
public class SupportEvalTests
{
    private static readonly string[] Classes = ["supported", "partial", "unsupported"];

    private static readonly IReadOnlyList<SupportExample> Examples = SupportEval.Load(Repo.Path("evals", "support-examples.jsonl"));

    [Fact]
    public void The_labelled_set_has_ten_of_each_class_in_groups_of_three()
    {
        Assert.Equal(30, Examples.Count);
        Assert.All(Classes, cls => Assert.Equal(10, Examples.Count(e => e.Expected == cls)));
        Assert.All(Examples.GroupBy(e => e.Group), g => Assert.Equal(Classes, g.Select(e => e.Expected)));
        Assert.All(Examples, e => Assert.False(string.IsNullOrWhiteSpace(e.CitedText)));
    }

    [Fact]
    public async Task A_judge_that_agrees_with_every_label_scores_thirty_of_thirty()
    {
        var replies = Examples.GroupBy(e => e.Group).Select(g => "{\"verdicts\":[" + string.Join(",", g.Select((e, i) => $"{{\"claim\":{i + 1},\"verdict\":\"{e.Expected}\",\"reason\":\"r\"}}")) + "]}").ToArray();
        var outcomes = await SupportEval.RunAsync(new ScriptedRuntime("", replies), null, Examples, default);
        var report = SupportEval.Report(outcomes);
        Assert.StartsWith("agreement 30/30", report, StringComparison.Ordinal);
        Assert.DoesNotContain("MISS", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Misses_are_listed_and_the_costly_ones_counted()
    {
        var replies = Examples.GroupBy(e => e.Group).Select((g, gi) => "{\"verdicts\":[" + string.Join(",", g.Select((e, i) =>
            $"{{\"claim\":{i + 1},\"verdict\":\"{(gi == 0 && i == 0 ? "unsupported" : gi == 0 && i == 2 ? "supported" : e.Expected)}\",\"reason\":\"r\"}}")) + "]}").ToArray();
        var outcomes = await SupportEval.RunAsync(new ScriptedRuntime("", replies), null, Examples, default);
        var report = SupportEval.Report(outcomes);
        Assert.StartsWith("agreement 28/30", report, StringComparison.Ordinal);
        Assert.Contains("supported claims that would be dropped: 1; unsupported claims kept as supported: 1", report, StringComparison.Ordinal);
        Assert.Equal(2, report.Split('\n').Count(l => l.StartsWith("MISS", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_group_the_judge_cannot_answer_counts_as_no_verdict()
    {
        var outcomes = await SupportEval.RunAsync(new ScriptedRuntime("", "no", "no"), null, Examples.Take(3).ToList(), default);
        Assert.All(outcomes, o => Assert.Null(o.Got));
        Assert.Contains("no verdict", SupportEval.Report(outcomes), StringComparison.Ordinal);
    }
}
