using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Modules.Intake.Api;

/// <summary>A Plane work item moved into a chosen state starts a driven batch of one task (ADR 0067). chargehand polls: the server is not reachable from Plane. Plane's states are named, not numbered,
/// in the profile: ids differ per project and per instance.</summary>
/// <param name="Url">The base URL of the Plane instance (<c>https://plane.example.test</c>).</param>
/// <param name="ApiKeySecret">The secret-store item that holds a Plane API key; sent as <c>X-API-Key</c>. The key's member must be able to read the projects and write comments and states.</param>
/// <param name="Workspace">The workspace slug.</param>
/// <param name="Projects">Plane project id to the checkout its work items are done in. Opt-in: a project not listed is never read.</param>
/// <param name="TriggerState">The state name that starts a batch (for example <c>Agent</c>).</param>
/// <param name="StartedState">The state name an item is moved to when its batch starts.</param>
/// <param name="ReviewState">The state name an item is moved to when its batch ends with a pull request.</param>
/// <param name="FailedState">The state name an item is moved to when its batch ends without one; null: the item stays in <paramref name="StartedState"/> and a comment says why.</param>
/// <param name="PollSeconds">How often the projects are read (default 120).</param>
/// <param name="BudgetClass">The size class of a triggered task (default M); it sets the governor's budget (ADR 0045).</param>
/// <param name="MaxTokensTotal">The token cap of a triggered batch (default 2,000,000).</param>
/// <param name="MaxUsdTotal">The dollar cap of a triggered batch (default 5).</param>
/// <param name="MaxConcurrent">Triggered batches running at once (default 2); a trigger over it waits for a slot.</param>
/// <param name="MaxStartsPerDay">Deprecated, ignored since 0.19.4 (ADR 0081): spend is bounded by <c>automation.account</c>'s dollar caps. Accepted so a profile that sets it still loads.</param>
public sealed partial record AutomationPlane(string? Url = null, string? ApiKeySecret = null, string? Workspace = null, IReadOnlyDictionary<string, PlaneProject>? Projects = null,
    string? TriggerState = null, string? StartedState = null, string? ReviewState = null, string? FailedState = null, int PollSeconds = 120, string BudgetClass = "M",
    long MaxTokensTotal = 2_000_000, decimal MaxUsdTotal = 5m, int MaxConcurrent = 2, int? MaxStartsPerDay = null)
{
    internal void Validate(bool drivenEnabled)
    {
        List<string> problems = [.. SourceProblems(drivenEnabled), .. StateProblems(), .. LimitProblems()];
        if (problems.Count > 0)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"automation.plane: {string.Join("; ", problems)}", "Fix automation.plane in the profile; docs/guide/intake.md lists the keys.");
    }

    private IEnumerable<string> SourceProblems(bool drivenEnabled)
    {
        if (!drivenEnabled)
            yield return "needs driven.enabled";
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            yield return "url must be an absolute http or https URL";
        if (string.IsNullOrWhiteSpace(ApiKeySecret))
            yield return "api_key_secret must name the secret item that holds the Plane API key";
        if (string.IsNullOrWhiteSpace(Workspace))
            yield return "workspace must be the workspace slug";
        if (Projects is not { Count: > 0 })
            yield return "projects must list at least one Plane project id";
        foreach (var (id, project) in Projects ?? new Dictionary<string, PlaneProject>())
        {
            if (string.IsNullOrWhiteSpace(id)) yield return "projects key must be a Plane project id";
            if (string.IsNullOrWhiteSpace(project?.Path)) yield return $"projects.{id}.path must be the server's checkout of the repository";
            if (project?.Remote is { } remote && !RepositoryName().IsMatch(remote)) yield return $"projects.{id}.remote must be owner/repo";
        }
    }

    private IEnumerable<string> StateProblems()
    {
        if (string.IsNullOrWhiteSpace(TriggerState)) yield return "trigger_state must be a state name";
        if (string.IsNullOrWhiteSpace(StartedState)) yield return "started_state must be a state name";
        if (string.IsNullOrWhiteSpace(ReviewState)) yield return "review_state must be a state name";
        if (FailedState is { } failed && string.IsNullOrWhiteSpace(failed)) yield return "failed_state must be a state name or left out";
        if (!string.IsNullOrWhiteSpace(TriggerState) && (Same(TriggerState, StartedState) || Same(TriggerState, ReviewState) || Same(TriggerState, FailedState)))
            yield return "trigger_state must differ from the states an item is moved to";
    }

    private IEnumerable<string> LimitProblems()
    {
        if (PollSeconds is < AutomationGitHub.MinPollSeconds or > 3600) yield return $"poll_seconds must be {AutomationGitHub.MinPollSeconds} to 3600";
        if (BudgetClass is not ("S" or "M" or "L")) yield return "budget_class must be S, M or L";
        if (MaxTokensTotal < 1) yield return "max_tokens_total must be above 0";
        if (MaxUsdTotal <= 0) yield return "max_usd_total must be above 0";
        if (MaxConcurrent is < 1 or > 10) yield return "max_concurrent must be 1 to 10";
    }

    private static bool Same(string a, string? b) => string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryName();
}

/// <param name="Path">The checkout the server reads the base commit from and the batch works from (ADR 0023); its <c>origin</c> is the repository.</param>
/// <param name="Remote">The repository as <c>owner/repo</c>, named in the comments chargehand writes; null: left out.</param>
public sealed record PlaneProject(string Path, string? Remote = null);
