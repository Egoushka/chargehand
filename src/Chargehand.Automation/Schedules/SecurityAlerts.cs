using System.Globalization;
using Chargehand.Contracts;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Automation;

/// <summary>An open code-scanning or Dependabot alert of a repository (CHARGEHAND-190).</summary>
/// <param name="Kind"><see cref="SecurityAlerts.CodeScanning"/> or <see cref="SecurityAlerts.Dependabot"/>.</param>
/// <param name="Severity">GitHub's word: critical, high, medium (moderate on an advisory), low; for a code-scanning rule without a security severity, error, warning or note.</param>
/// <param name="Title">One line: the rule's description, or the advisory's summary.</param>
/// <param name="Detail">Where and what: the file and line and the message, or the package, its manifest and the vulnerable and patched versions.</param>
public sealed record SecurityAlert(string Kind, int Number, string Severity, string Title, string Detail, string? Url)
{
    /// <summary>The alert's handle in the schedule state and the task id: <c>dependabot-7</c>.</summary>
    public string Key => $"{Kind}-{Number.ToString(CultureInfo.InvariantCulture)}";
}

/// <param name="Skipped">Each kind that could not be read, with why (not enabled for the repository, or the App lacks the permission).</param>
public sealed record SecurityAlertsRead(IReadOnlyList<SecurityAlert> Alerts, IReadOnlyList<string> Skipped);

/// <summary>Reads a repository's open alerts as the GitHub App: read-only.</summary>
public interface ISecurityAlerts
{
    /// <param name="repository"><c>owner/repo</c>.</param>
    /// <exception cref="IntakeException">GitHub could not be reached or answered with something other than a refusal of one kind.</exception>
    Task<SecurityAlertsRead> OpenAlertsAsync(string repository, CancellationToken ct);
}

/// <summary>The <c>security-alerts</c> template (CHARGEHAND-190): which open alerts become tasks this run, and their tasks.</summary>
public static class SecurityAlerts
{
    public const string CodeScanning = "code-scanning";
    public const string Dependabot = "dependabot";
    public const int MaxText = 1500;

    /// <summary>The alerts not taken before, most severe first, at most <paramref name="max"/>.</summary>
    public static IReadOnlyList<SecurityAlert> Fresh(IEnumerable<SecurityAlert> open, IReadOnlySet<string> taken, int max) =>
        [.. open.Where(a => !taken.Contains(a.Key)).OrderBy(a => Rank(a.Severity)).ThenBy(a => a.Kind, StringComparer.Ordinal).ThenBy(a => a.Number).Take(max)];

    public static DrivenTask TaskFor(SecurityAlert alert, string goal, string size) =>
        new($"alert-{alert.Key}", Goal: $"{goal}\n\n{Describe(alert)}", Size: size, Title: Cut($"Fix {Name(alert)}: {alert.Title}", 72));

    public static string Describe(SecurityAlert alert) =>
        Cut($"{Name(alert)}, severity {alert.Severity}: {alert.Title}\n{alert.Detail}{(alert.Url is { } url ? $"\n{url}" : "")}", MaxText);

    private static string Name(SecurityAlert alert) =>
        $"{(alert.Kind == Dependabot ? "Dependabot" : "code-scanning")} alert #{alert.Number.ToString(CultureInfo.InvariantCulture)}";

    private static int Rank(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => 0,
        "high" or "error" => 1,
        "medium" or "moderate" => 2,
        "low" or "warning" => 3,
        "note" => 4,
        _ => 5,
    };

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    /// <summary><c>owner/repo</c> of the checkout's <c>origin</c> when it is on GitHub; null otherwise.</summary>
    public static async Task<string?> RepositoryOfAsync(string checkoutPath, CancellationToken ct)
    {
        var origin = await GitRepositoryHeads.GitAsync(checkoutPath, ["remote", "get-url", "origin"], ct);
        return origin.ExitCode == 0 && Modules.Forge.Api.RemoteRepository.Parse(origin.Output, null) is { Forge: Modules.Forge.Api.ForgeKind.GitHub } remote ? $"{remote.Owner}/{remote.Name}" : null;
    }
}
