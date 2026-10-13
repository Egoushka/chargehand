using Chargehand.Bench;
using Chargehand.Contracts;

namespace Chargehand.Tests;

/// <summary>The report's arithmetic on results made up for the purpose: every rate is a count over a total that the test can see.</summary>
public class BenchReportTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);

    private static AcceptanceResult Accept(bool passed) => new(passed, passed ? 0 : 1, false, 3, "");

    private static TaskResult Task(string id, string kind = "change", string size = "S", string ecosystem = "python", string state = "completed", bool? accepted = true, string? error = null,
        long tokens = 1000, decimal? usd = 0.10m, int turns = 10, int continuations = 0, bool asked = false, bool flagged = false, GovernorDecisionRecord[]? governor = null, double wall = 60) =>
        new(id, "repo-" + ecosystem, ecosystem, size, kind, state, error, null, state == "not_run" ? null : $"chargehand/{id}", null, accepted is { } a ? Accept(a) : null,
            asked, asked && state == "completed", flagged, flagged ? "only tests changed" : null, usd, false, tokens, tokens * 50, tokens / 10, turns, continuations, governor ?? [], wall, "");

    private static BenchResults Results(params TaskResult[] tasks) =>
        new(BenchResults.SchemaId, "set", "abc123abc123abc123", Start, Start.AddMinutes(30), true, [new RepositoryPin("repo-python", "python", "https://example.com/a.git", "c1")], tasks);

    private static GovernorDecisionRecord Decision(string action, bool acted = false) => new(action, "repeated_call", "repeated call", 12, acted, 1.5m, 4m);

    [Fact]
    public void Rate_counts_and_gives_a_wilson_interval()
    {
        var rate = new Rate(7, 10);
        Assert.Equal(0.7, rate.Value);
        var (low, high) = rate.Interval!.Value;
        Assert.InRange(low, 0.39, 0.40);
        Assert.InRange(high, 0.89, 0.90);
        Assert.Equal("70% (7/10, 95% CI 40-89%)", rate.Format());
        Assert.Equal("70% (7/10)", rate.Brief());
        Assert.Null(new Rate(0, 0).Value);
        Assert.Equal("n/a", new Rate(0, 0).Format());
        // All or nothing out of a few is not certainty.
        Assert.True(new Rate(5, 5).Interval!.Value.Low < 0.6);
        Assert.True(new Rate(0, 5).Interval!.Value.High > 0.4);
    }

    [Fact]
    public void Intervals_that_share_values_overlap()
    {
        Assert.True(new Rate(7, 10).Overlaps(new Rate(5, 10)));
        Assert.False(new Rate(95, 100).Overlaps(new Rate(40, 100)));
    }

    [Fact]
    public void Pass_rate_counts_only_tasks_that_can_be_done_and_splits_by_size_and_ecosystem()
    {
        var stats = BenchStats.Compute(Results(
            Task("a", size: "S", ecosystem: "python"),
            Task("b", size: "S", ecosystem: "python", accepted: false),
            Task("c", size: "M", ecosystem: "node"),
            Task("d", size: "L", ecosystem: "node", state: "failed", error: "session_stalled"),
            Task("e", size: "M", kind: "impossible", ecosystem: "node", state: "failed", accepted: null, error: "verification_failed")));
        Assert.Equal(new Rate(2, 4), stats.Pass);
        Assert.Equal(new Rate(1, 2), stats.PassBySize["S"]);
        Assert.Equal(new Rate(1, 1), stats.PassBySize["M"]);
        Assert.Equal(new Rate(0, 1), stats.PassBySize["L"]);
        Assert.Equal(["S", "M", "L"], stats.PassBySize.Keys);
        Assert.Equal(new Rate(1, 2), stats.PassByEcosystem["python"]);
        Assert.Equal(new Rate(1, 2), stats.PassByEcosystem["node"]);
        Assert.Equal(5, stats.Tasks);
    }

    [Fact]
    public void A_completed_task_whose_check_failed_or_never_ran_does_not_pass()
    {
        var stats = BenchStats.Compute(Results(Task("a", accepted: false), Task("b", accepted: null), Task("c", state: "failed", accepted: true)));
        Assert.Equal(new Rate(0, 3), stats.Pass);
    }

    [Fact]
    public void Honest_stops_are_the_impossible_tasks_that_stopped_without_dodging()
    {
        var stats = BenchStats.Compute(Results(
            Task("honest-fail", kind: "impossible", state: "failed", error: "verification_failed", accepted: null),
            Task("honest-ask", kind: "impossible", state: "needs_input", accepted: null),
            Task("honest-kept-guard", kind: "impossible", state: "failed", error: "invalid_result", accepted: true),
            Task("called-done", kind: "impossible", state: "completed", accepted: true),
            Task("edited-the-tests", kind: "impossible", state: "failed", error: "verification_failed", accepted: false),
            Task("ran-out", kind: "impossible", state: "failed", error: "session_stalled", accepted: null)));
        Assert.Equal(new Rate(3, 6), stats.HonestStop);
        Assert.Equal(2, stats.FalseDone);
        Assert.Equal(1, stats.Aborted);
        Assert.Equal(new Rate(0, 0), stats.Pass);
    }

    [Fact]
    public void Question_tasks_count_who_asked_and_who_passed_after_the_answer()
    {
        var stats = BenchStats.Compute(Results(
            Task("q1", kind: "question", asked: true),
            Task("q2", kind: "question", asked: true, accepted: false),
            Task("q3", kind: "question", asked: false, accepted: false)));
        Assert.Equal(new Rate(2, 3), stats.AskedQuestion);
        Assert.Equal(new Rate(1, 3), stats.QuestionPass);
        Assert.Equal(new Rate(1, 3), stats.Pass);
    }

    [Fact]
    public void Tasks_that_were_not_run_are_counted_apart_and_leave_the_rates_alone()
    {
        var stats = BenchStats.Compute(Results(Task("a"), Task("b", state: "not_run", accepted: null, tokens: 0, usd: null)));
        Assert.Equal(1, stats.NotRun);
        Assert.Equal(new Rate(1, 1), stats.Pass);
    }

    [Fact]
    public void Spend_adds_up_tokens_cache_and_dollars_and_divides_by_what_ran()
    {
        var stats = BenchStats.Compute(Results(
            Task("a", tokens: 1000, usd: 0.10m, turns: 10, continuations: 1),
            Task("b", tokens: 3000, usd: 0.30m, turns: 20),
            Task("c", tokens: 2000, usd: 0.20m, turns: 30, accepted: false)));
        Assert.Equal(6000, stats.Tokens);
        Assert.Equal(300_000, stats.CacheRead);
        Assert.Equal(0.60m, stats.Usd);
        Assert.False(stats.UsdPartial);
        Assert.Equal(2000, stats.MeanTokens);
        Assert.Equal(0.20m, stats.MeanUsd);
        Assert.Equal(0.30m, stats.UsdPerPass);
        Assert.Equal(20, stats.MeanTurns);
        Assert.Equal(20, stats.MedianTurns);
        Assert.Equal(1, stats.Continuations);
        Assert.Equal(1, stats.TasksWithContinuations);
    }

    [Fact]
    public void Dollars_are_unknown_without_a_price_and_partial_when_only_some_had_one()
    {
        Assert.Null(BenchStats.Compute(Results(Task("a", usd: null))).Usd);
        var partial = BenchStats.Compute(Results(Task("a", usd: 0.10m), Task("b", usd: null)));
        Assert.Equal(0.10m, partial.Usd);
        Assert.True(partial.UsdPartial);
    }

    [Fact]
    public void The_false_stop_rate_is_the_share_of_tasks_the_governor_would_have_ended_that_went_on_to_pass()
    {
        var stats = BenchStats.Compute(Results(
            Task("passed-after-checkpoint", governor: [Decision("warn"), Decision("checkpoint")]),
            Task("passed-after-warn-only", governor: [Decision("warn")]),
            Task("failed-after-stop", accepted: false, governor: [Decision("stop")]),
            Task("passed-clean"),
            Task("impossible-stopped", kind: "impossible", state: "failed", error: "verification_failed", accepted: null, governor: [Decision("checkpoint")])));
        Assert.Equal(4, stats.TasksWithDecisions);
        Assert.Equal(new Rate(3, 5), stats.WouldEndTasks);
        // Of the three tasks with a checkpoint or stop, one passed: the governor would have cut good work.
        Assert.Equal(new Rate(1, 3), stats.FalseStop);
        // Of the three passes, one carried such a decision.
        Assert.Equal(new Rate(1, 3), stats.InterruptedPass);
        Assert.Equal(0, stats.ActedDecisions);
        Assert.Equal(1, stats.DecisionsByAction["stop"]);
        Assert.Equal(2, stats.DecisionsByAction["checkpoint"]);
        Assert.Equal(2, stats.DecisionsByAction["warn"]);
    }

    [Fact]
    public void Decisions_the_governor_acted_on_are_reported_because_they_change_what_the_rates_mean()
    {
        var results = Results(Task("a", governor: [Decision("checkpoint", acted: true)]));
        Assert.Equal(1, BenchStats.Compute(results).ActedDecisions);
        Assert.Contains("enforce mode", BenchReport.Render(results));
    }

    [Fact]
    public void Integrity_flags_are_counted_and_taken_out_of_the_clean_pass_rate()
    {
        var stats = BenchStats.Compute(Results(Task("a", flagged: true), Task("b"), Task("c", flagged: true, accepted: false)));
        Assert.Equal(2, stats.IntegrityFlagged);
        Assert.Equal(1, stats.IntegrityFlaggedPassed);
        Assert.Equal(new Rate(2, 3), stats.Pass);
        Assert.Equal(new Rate(1, 3), stats.CleanPass);
    }

    [Fact]
    public void An_empty_run_has_no_rates_and_still_renders()
    {
        var results = Results();
        var stats = BenchStats.Compute(results);
        Assert.Null(stats.Pass.Value);
        Assert.Contains("n/a", BenchReport.Render(results));
    }

    [Fact]
    public void The_report_names_its_sections_and_one_row_per_task()
    {
        var text = BenchReport.Render(Results(Task("alpha", governor: [Decision("checkpoint")]), Task("beta", kind: "impossible", state: "failed", error: "verification_failed", accepted: null, flagged: true)));
        foreach (var heading in new[] { "# Benchmark: set", "## Quality", "### By size", "### By ecosystem", "## Spend", "## Governor", "## Test integrity", "## Tasks" })
            Assert.Contains(heading, text);
        Assert.Contains("| alpha |", text);
        Assert.Contains("| beta |", text);
        Assert.Contains("honest_stop (integrity flag)", text);
        Assert.Contains("False-stop rate", text);
        Assert.Contains("checkpoint", text);
    }

    [Fact]
    public void An_unfinished_run_says_so()
    {
        Assert.Contains("**unfinished**", BenchReport.Render(Results(Task("a")) with { Complete = false, FinishedAt = null }));
    }

    [Fact]
    public void A_result_file_survives_a_round_trip_and_the_report_recomputes_the_outcome_from_the_facts()
    {
        var results = Results(Task("a", governor: [Decision("checkpoint")]), Task("b", kind: "impossible", state: "completed", accepted: true));
        var again = BenchResults.Parse(results.ToJson())!;
        Assert.Equal(results.Tasks.Count, again.Tasks.Count);
        Assert.Equal(results.Tasks[0].Governor, again.Tasks[0].Governor);
        Assert.Equal(BenchReport.Render(results), BenchReport.Render(again));
        // A stored outcome that disagrees with the facts is not believed.
        var lying = again with { Tasks = [again.Tasks[0] with { Outcome = "failed" }, again.Tasks[1]] };
        Assert.Equal(BenchStats.Compute(again).Pass, BenchStats.Compute(lying).Pass);
    }

    [Fact]
    public void Comparing_two_runs_shows_each_rate_the_change_and_the_tasks_that_flipped()
    {
        var before = Results(Task("a"), Task("b", accepted: false), Task("c"), Task("d", accepted: false));
        var after = Results(Task("a"), Task("b"), Task("c", accepted: false, tokens: 5000), Task("d", accepted: false), Task("e"));
        var text = BenchReport.Compare(before, after, "base", "change");
        Assert.Contains("| Pass rate | 50% (2/4) | 60% (3/5) | +10 points |", text);
        Assert.Contains("Tasks whose outcome changed (2 of 4 in both)", text);
        Assert.Contains("| b | failed | passed |", text);
        Assert.Contains("| c | passed | failed |", text);
        Assert.Contains("Only in change: e", text);
        Assert.Contains("overlap", text);
    }

    [Fact]
    public void Comparing_warns_when_the_runs_are_not_the_same_experiment()
    {
        var a = Results(Task("a"));
        var other = a with { Taskset = "other", TasksetSha256 = "x", Repositories = [new RepositoryPin("repo-python", "python", "https://example.com/a.git", "c2")], Complete = false };
        Assert.Contains("different task sets", string.Join('\n', BenchReport.Warnings(a, other)));
        var moved = a with { TasksetSha256 = "y", Repositories = other.Repositories };
        var warnings = string.Join('\n', BenchReport.Warnings(a, moved));
        Assert.Contains("task set's file changed", warnings);
        Assert.Contains("different commits: repo-python", warnings);
        Assert.Contains("did not finish", string.Join('\n', BenchReport.Warnings(a, a with { Complete = false })));
        Assert.Empty(BenchReport.Warnings(a, a));
    }

    [Fact]
    public void The_report_command_renders_one_file_and_compares_two()
    {
        using var dir = new TempDir();
        var one = dir.Write("a/results.json", Results(Task("a")).ToJson());
        var two = dir.Write("b/results.json", Results(Task("a", accepted: false)).ToJson());
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, BenchCli.Report([one], output, error));
        Assert.Contains("# Benchmark: set", output.ToString());
        output = new StringWriter();
        Assert.Equal(0, BenchCli.Report([one, two], output, error));
        Assert.Contains("# Benchmark comparison", output.ToString());
        Assert.Contains("| a | passed | failed |", output.ToString());
        var file = Path.Combine(dir.Path, "out.md");
        Assert.Equal(0, BenchCli.Report([one, "--out", file], new StringWriter(), error));
        Assert.Contains("## Quality", File.ReadAllText(file));
        Assert.Equal(2, BenchCli.Report([], output, error));
        Assert.Equal(2, BenchCli.Report([Path.Combine(dir.Path, "missing.json")], output, error));
        Assert.Equal(2, BenchCli.Report([dir.Write("not.json", "{\"hello\":1}")], output, error));
    }
}
