namespace Chargehand.Modules.Intake.Api;

/// <summary>How a scheduled entry runs: a driven batch of one task (writes, opens a pull request), a read-only run whose answer is posted, or a report built from this server's own data.</summary>
public enum ScheduleKind { Driven, ReadOnly, Report }

/// <param name="Goal">The text the worker gets; null for the report, which makes no model call.</param>
public sealed record ScheduleTemplate(string Name, ScheduleKind Kind, string? Goal);

/// <summary>The built-in goals of <c>automation.schedules</c> (ADR 0068). Constants, not files: the release has nothing extra to ship and a goal cannot drift from the code that runs it.</summary>
public static class ScheduleTemplates
{
    public const string DependencyUpgrades = "dependency-upgrades";
    public const string DocDrift = "doc-drift";
    public const string FlakyTests = "flaky-tests";
    public const string MorningReport = "morning-report";

    private static readonly ScheduleTemplate[] All =
    [
        new(DependencyUpgrades, ScheduleKind.Driven,
            "Update this repository's dependencies to their latest compatible minor and patch versions. Do not take a major version. Run the repository's tests after the update. "
            + "Open one pull request. In its description list each dependency that changed (old and new version) and each one you skipped, with the reason."),
        new(DocDrift, ScheduleKind.ReadOnly,
            "Find statements in README.md and in docs/ that the current code contradicts: a command, option, default, path, name or behaviour the code no longer has. "
            + "Report each finding with the doc file and line and the code file and line that contradicts it. Report nothing you could not tie to a line of each. If you find none, say so."),
        new(FlakyTests, ScheduleKind.ReadOnly,
            "Find tests likely to be flaky by reading the test code: waits and sleeps on the clock, assertions that depend on ordering or on timing, state shared between tests, "
            + "ports, files or environment variables a test assumes are free. You have no access to CI history, so you cannot see runs that passed and failed on the same commit: say that in your answer, "
            + "and report each suspect as a pattern at a test file and line, not as a measured flake."),
        new(MorningReport, ScheduleKind.Report, null),
    ];

    public static IReadOnlyList<string> Names => [.. All.Select(t => t.Name)];

    public static ScheduleTemplate? Find(string? name) => All.FirstOrDefault(t => t.Name == name);
}
