namespace Chargehand.Modules.Oversight.Api;

/// <summary>One repository's earned autonomy (ADR 0097) for <c>GET /v1/learn</c>: the grant, and what would widen or narrow it.</summary>
/// <param name="Widens">What the next right needs, as a sentence; null when auto-merge is already earned.</param>
/// <param name="Narrows">What would take the rights back, as a sentence.</param>
public sealed record AutonomyEntry(string Repository, string MaxSizeClass, bool AutoMerge, int CleanStreak, string? LostTo, string? Widens, string Narrows);

/// <summary>The grant of every repository the entries mention. Pure: the same entries and time give the same list.</summary>
public static class AutonomyOverview
{
    public static IReadOnlyList<AutonomyEntry> Build(DrivenGovernor settings, IReadOnlyList<LearningEntry> entries, DateTimeOffset now)
    {
        var autonomy = settings.Autonomy ?? new DrivenGovernorAutonomy();
        return [.. entries.Where(e => !e.Record.Bench).Select(e => e.Record.Repository).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(repository =>
        {
            var grant = EarnedAutonomy.Assess(settings, repository, entries, now);
            return new AutonomyEntry(repository, grant.MaxSizeClass, grant.AutoMerge, grant.CleanStreak, grant.LostTo, Widens(autonomy, grant),
                $"A revert or a failed check resets the streak to 0 and takes back size L and auto-merge (window {autonomy.WindowDays} days).");
        })];
    }

    private static string? Widens(DrivenGovernorAutonomy autonomy, AutonomyGrant grant) =>
        grant.AutoMerge ? null
        : grant.MaxSizeClass == "M" ? $"{autonomy.LargeAfter - grant.CleanStreak} more pull requests merged unchanged earn size L; {autonomy.AutoMergeAfter - grant.CleanStreak} earn auto-merge."
        : $"{autonomy.AutoMergeAfter - grant.CleanStreak} more pull requests merged unchanged earn auto-merge.";
}
