namespace Chargehand.Modules.Oversight.Api;

/// <summary>ADR 0097's thresholds, the starting values: a repository's clean streak is its pull requests merged unchanged since its last revert or failed check, within the last
/// <paramref name="WindowDays"/> days. At <paramref name="LargeAfter"/> it earns the L size class, at <paramref name="AutoMergeAfter"/> auto-merge as well; a revert or a failed check
/// resets the streak to 0, which takes both back.</summary>
public sealed record DrivenGovernorAutonomy(int LargeAfter = 5, int AutoMergeAfter = 10, int WindowDays = 90)
{
    internal void Validate(List<string> problems)
    {
        if (LargeAfter < 1) problems.Add("autonomy.large_after must be at least 1");
        if (AutoMergeAfter < 1) problems.Add("autonomy.auto_merge_after must be at least 1");
        if (WindowDays < 1) problems.Add("autonomy.window_days must be at least 1");
    }
}

/// <summary>The rights a repository has earned (ADR 0097).</summary>
/// <param name="MaxSizeClass">The largest size class a task may take: <c>M</c> until the streak earns <c>L</c>.</param>
/// <param name="AutoMerge">The streak earned auto-merge.</param>
/// <param name="CleanStreak">Pull requests merged unchanged since the last loss, in the window.</param>
/// <param name="LostTo">What reset the streak last (<see cref="EarnedAutonomy.Revert"/> or <see cref="EarnedAutonomy.FailedCheck"/>); null: nothing in the window.</param>
public sealed record AutonomyGrant(string MaxSizeClass, bool AutoMerge, int CleanStreak, string? LostTo);

/// <summary>Earned autonomy (ADR 0097): rights grow with a clean record and shrink on reverts. Pure: the learning entries and the time are passed in. A pull request merged unchanged counts at the
/// time its outcome was read; a task whose session ended <c>checks_failed</c> counts as a loss when it ended, a pull request read back as <see cref="PrOutcomes.Reverted"/> when that was read. Merged
/// after edits, closed and open pull requests neither earn nor lose.</summary>
public static class EarnedAutonomy
{
    public const string Revert = "revert";
    public const string FailedCheck = "failed_check";

    /// <summary>The stop reason of a session whose checks still failed after their retries (<c>SessionDriver.ChecksFailed</c>, a module this one does not reference).</summary>
    private const string ChecksFailed = "checks_failed";

    public static AutonomyGrant Assess(DrivenGovernor settings, string repository, IReadOnlyList<LearningEntry> entries, DateTimeOffset now)
    {
        var autonomy = settings.Autonomy ?? new DrivenGovernorAutonomy();
        var since = now.AddDays(-autonomy.WindowDays);
        var events = entries.Where(e => e.Record.Repository == repository && !e.Record.Bench).SelectMany(Events).Where(e => e.At >= since && e.At <= now).OrderBy(e => e.At);
        var (streak, lostTo) = (0, (string?)null);
        foreach (var (_, loss) in events)
            (streak, lostTo) = loss is null ? (streak + 1, lostTo) : (0, loss);
        return new AutonomyGrant(streak >= autonomy.LargeAfter ? "L" : "M", streak >= autonomy.AutoMergeAfter, streak, lostTo);
    }

    /// <summary>A task's events: (when, null) for a clean merge, (when, what) for a loss.</summary>
    private static IEnumerable<(DateTimeOffset At, string? Loss)> Events(LearningEntry entry)
    {
        if (entry.Record.StopReason == ChecksFailed)
            yield return (entry.Record.At, FailedCheck);
        switch (entry.PrOutcome)
        {
            case { Outcome: PrOutcomes.MergedUnchanged } merged:
                yield return (merged.At, null);
                break;
            case { Outcome: PrOutcomes.Reverted } reverted:
                yield return (reverted.At, Revert);
                break;
        }
    }
}
