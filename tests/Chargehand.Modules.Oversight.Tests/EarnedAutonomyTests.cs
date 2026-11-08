using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Oversight.Tests;

/// <summary>ADR 0097: a repository earns the L size class and auto-merge from pull requests merged unchanged, and loses both on a revert or a failed check. The entries are synthetic.</summary>
public class EarnedAutonomyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private const string Repo = "o/r";

    private static readonly DrivenGovernor Starting = new();

    private static int _run;

    private static LearningEntry Entry(int daysAgo, string? prOutcome, string stop = "completed", string repository = Repo, bool bench = false)
    {
        var run = $"run-{++_run}";
        var at = Now.AddDays(-daysAgo);
        var record = new LearningRecord("batch", $"task-{run}", run, at, repository, "M", "caller", new LearningBudget(4m, new BudgetSource("flat")),
            new LearningSpend(1m, false, 10, 5, 0, 0, 3, 0), stop == "completed" ? LearningOutcome.Completed : LearningOutcome.Failed, stop, "observe", [], false, Bench: bench);
        return new LearningEntry(record, prOutcome is null ? null : new PrOutcomeRecord(run, at.AddHours(1), prOutcome, "aaaa"));
    }

    private static List<LearningEntry> Clean(int count, int fromDaysAgo = 30) => [.. Enumerable.Range(0, count).Select(i => Entry(fromDaysAgo - i, PrOutcomes.MergedUnchanged))];

    private static AutonomyGrant Assess(IReadOnlyList<LearningEntry> entries, DrivenGovernor? settings = null) => EarnedAutonomy.Assess(settings ?? Starting, Repo, entries, Now);

    [Fact]
    public void The_starting_values_are_the_adrs()
    {
        Assert.Equal(new DrivenGovernorAutonomy(LargeAfter: 5, AutoMergeAfter: 10, WindowDays: 90), new DrivenGovernorAutonomy());
    }

    [Fact]
    public void A_repository_with_no_record_has_no_wider_rights()
    {
        Assert.Equal(new AutonomyGrant("M", false, 0, null), Assess([]));
    }

    [Fact]
    public void Merges_without_change_earn_the_large_class_and_then_auto_merge()
    {
        Assert.Equal(new AutonomyGrant("M", false, 4, null), Assess(Clean(4)));
        Assert.Equal(new AutonomyGrant("L", false, 5, null), Assess(Clean(5)));
        Assert.Equal(new AutonomyGrant("L", false, 9, null), Assess(Clean(9)));
        Assert.Equal(new AutonomyGrant("L", true, 10, null), Assess(Clean(10)));
    }

    [Fact]
    public void The_governor_reads_the_thresholds_from_its_settings()
    {
        var strict = new DrivenGovernor(Autonomy: new DrivenGovernorAutonomy(LargeAfter: 2, AutoMergeAfter: 3));

        Assert.Equal(new AutonomyGrant("L", false, 2, null), Assess(Clean(2), strict));
        Assert.Equal(new AutonomyGrant("L", true, 3, null), Assess(Clean(3), strict));
    }

    [Fact]
    public void A_revert_takes_back_every_right_and_the_streak_starts_over()
    {
        List<LearningEntry> entries = [.. Clean(10, fromDaysAgo: 40), Entry(20, PrOutcomes.Reverted)];

        Assert.Equal(new AutonomyGrant("M", false, 0, EarnedAutonomy.Revert), Assess(entries));
        Assert.Equal(new AutonomyGrant("M", false, 3, EarnedAutonomy.Revert), Assess([.. entries, .. Clean(3, fromDaysAgo: 10)]));
    }

    [Fact]
    public void A_failed_check_takes_back_every_right()
    {
        List<LearningEntry> entries = [.. Clean(10, fromDaysAgo: 40), Entry(5, null, stop: "checks_failed")];

        Assert.Equal(new AutonomyGrant("M", false, 0, EarnedAutonomy.FailedCheck), Assess(entries));
    }

    [Fact]
    public void Edited_closed_and_open_pull_requests_neither_earn_nor_lose()
    {
        List<LearningEntry> entries = [.. Clean(5, fromDaysAgo: 40), Entry(10, PrOutcomes.MergedEdited), Entry(9, PrOutcomes.Closed), Entry(8, PrOutcomes.Open), Entry(7, null, stop: "turn_limit")];

        Assert.Equal(new AutonomyGrant("L", false, 5, null), Assess(entries));
    }

    [Fact]
    public void Only_the_repositorys_own_record_inside_the_window_counts_and_benchmarks_never_do()
    {
        List<LearningEntry> entries =
        [
            .. Enumerable.Range(0, 10).Select(i => Entry(100 + i, PrOutcomes.MergedUnchanged)),
            .. Enumerable.Range(0, 10).Select(i => Entry(10 + i, PrOutcomes.MergedUnchanged, repository: "o/other")),
            .. Enumerable.Range(0, 10).Select(i => Entry(10 + i, PrOutcomes.MergedUnchanged, bench: true)),
            Entry(5, PrOutcomes.Reverted, repository: "o/other"),
        ];

        Assert.Equal(new AutonomyGrant("M", false, 0, null), Assess(entries));
    }

    [Fact]
    public void A_revert_older_than_the_window_no_longer_counts()
    {
        List<LearningEntry> entries = [Entry(120, PrOutcomes.Reverted), .. Clean(5)];

        Assert.Equal(new AutonomyGrant("L", false, 5, null), Assess(entries));
    }

    [Fact]
    public void Thresholds_that_cannot_work_are_refused()
    {
        foreach (var bad in new[] { new DrivenGovernorAutonomy(LargeAfter: 0), new DrivenGovernorAutonomy(AutoMergeAfter: 0), new DrivenGovernorAutonomy(WindowDays: 0) })
            Assert.ThrowsAny<Exception>(() => new DrivenGovernor(Autonomy: bad).Validate());
        new DrivenGovernor(Autonomy: new DrivenGovernorAutonomy()).Validate();
    }
}
