namespace Chargehand.Automation;

/// <summary>The <c>claim-drift</c> text (specs/2026-11-11-claim-drift-watch.md): no model call, only the citation check over the working tree. Each finding is posted once; a finding that is fixed is forgotten so it can come back.</summary>
public static class ClaimDriftReport
{
    public static string Title(DateTimeOffset now) => $"chargehand claim drift {now.UtcDateTime:yyyy-MM-dd}";

    public static IReadOnlyList<DriftFinding> Findings(string repositoryRoot) => ClaimDrift.Check(ClaimDrift.Scan(repositoryRoot), repositoryRoot);

    /// <summary>The findings not in <paramref name="reported"/>, as the text to post; null when there is nothing new.</summary>
    public static string? Format(IReadOnlyList<DriftFinding> findings, IReadOnlyCollection<string> reported)
    {
        var fresh = findings.Where(f => !reported.Contains(f.Key)).ToList();
        if (fresh.Count == 0)
            return null;
        return ScheduleResults.Cut($"{fresh.Count} guide citation(s) no longer match the code:\n" + string.Join('\n', fresh.Select(f => "- " + f.Line)));
    }
}
