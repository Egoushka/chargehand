using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Tests;

/// <summary>The agent-readiness score: a weighted mean of the signals that have data, a gap line per weak signal, and a proposed task only when there is a gap.</summary>
public class RepositoryReadinessTests
{
    private static readonly string[] Tests = ["sh", "scripts/check.sh"];

    private static LearningEntry Entry(int n, string outcome = LearningOutcome.Completed, string? pr = null) =>
        new(LearningTests.Record($"r{n}", outcome: outcome), pr is null ? null : new PrOutcomeRecord($"r{n}", DateTimeOffset.UnixEpoch, pr, null));

    private static ReadinessInput Input(IReadOnlyList<LearningEntry>? entries = null, IReadOnlyList<string>? verify = null, IReadOnlyList<string>? proposed = null, IReadOnlyList<bool>? firstCi = null,
        bool? ci = true) =>
        new("o/r", entries ?? [], verify, proposed, firstCi ?? [], ci);

    [Fact]
    public void A_repository_whose_tasks_complete_and_whose_checks_pass_scores_100_with_no_gap_and_no_proposal()
    {
        var readiness = RepositoryReadiness.Score(Input([Entry(1, pr: PrOutcomes.MergedUnchanged), Entry(2), Entry(3)], verify: Tests, firstCi: [true, true]));

        Assert.Equal((100, false), (readiness.Score, readiness.Below));
        Assert.Empty(readiness.Gaps);
        Assert.Null(readiness.ProposalGoal("o/r"));
    }

    [Fact]
    public void Signals_with_no_data_do_not_count_so_a_new_repository_is_scored_on_its_setup_alone()
    {
        var readiness = RepositoryReadiness.Score(Input([Entry(1), Entry(2)], verify: Tests));

        Assert.Equal(100, readiness.Score);
        Assert.Equal(["completion", "first_ci", "human_fix"], readiness.Signals.Where(s => s.Value is null).Select(s => s.Name));
        Assert.Contains("too few to rate", readiness.Signals[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repository_where_most_tasks_fail_falls_below_the_threshold_and_the_proposal_lists_every_gap()
    {
        // completion 1/5 (35), verify build-only 0.5 (20), first CI 0/2 (15), a person changed 2 of 2 merges (15), no CI (15): (7 + 10 + 0 + 0 + 0) / 100.
        var entries = new[]
        {
            Entry(1, pr: PrOutcomes.MergedEdited), Entry(2, LearningOutcome.Failed, PrOutcomes.MergedEdited), Entry(3, LearningOutcome.Failed), Entry(4, LearningOutcome.Partial),
            Entry(5, LearningOutcome.NeedsInput),
        };
        var readiness = RepositoryReadiness.Score(Input(entries, verify: ["dotnet", "build", "-warnaserror"], firstCi: [false, false], ci: false));

        Assert.Equal((17, true), (readiness.Score, readiness.Below));
        Assert.Equal(5, readiness.Gaps.Count);
        Assert.Contains("4 of 5 driven tasks did not complete", readiness.Gaps[0], StringComparison.Ordinal);
        Assert.Contains("builds but runs no tests", readiness.Gaps[1], StringComparison.Ordinal);
        var goal = readiness.ProposalGoal("o/r")!;
        Assert.StartsWith("Make o/r ready for coding agents.", goal, StringComparison.Ordinal);
        Assert.All(readiness.Gaps, g => Assert.Contains($"- {g}\n", goal, StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_set_command_a_proposed_one_counts_half_and_none_counts_nothing()
    {
        Assert.Equal(0.5, RepositoryReadiness.Score(Input(proposed: Tests)).Signals.Single(s => s.Name == "verify").Value);
        Assert.Equal(0, RepositoryReadiness.Score(Input()).Signals.Single(s => s.Name == "verify").Value);
        Assert.Null(RepositoryReadiness.Score(Input(ci: null)).Signals.Single(s => s.Name == "ci").Value);
    }

    [Theory]
    [InlineData("dotnet test", true)]
    [InlineData("npm run test", true)]
    [InlineData("sh scripts/check.sh", true)]
    [InlineData("npx vitest run", true)]
    [InlineData("dotnet build -warnaserror", false)]
    [InlineData("npm run lint", false)]
    public void A_command_runs_tests_when_a_word_names_a_test_runner_or_a_check(string command, bool expected) =>
        Assert.Equal(expected, RepositoryReadiness.RunsTests(command.Split(' ')));

    [Fact]
    public void A_workflow_file_or_a_known_ci_file_counts_as_ci()
    {
        var dir = Directory.CreateTempSubdirectory("readiness-").FullName;
        try
        {
            Assert.False(RepositoryReadiness.CiPresent(dir));
            Directory.CreateDirectory(Path.Combine(dir, ".github", "workflows"));
            Assert.False(RepositoryReadiness.CiPresent(dir));
            File.WriteAllText(Path.Combine(dir, ".github", "workflows", "ci.yaml"), "on: push\n");
            Assert.True(RepositoryReadiness.CiPresent(dir));
            File.Delete(Path.Combine(dir, ".github", "workflows", "ci.yaml"));
            File.WriteAllText(Path.Combine(dir, ".gitlab-ci.yml"), "test:\n");
            Assert.True(RepositoryReadiness.CiPresent(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
