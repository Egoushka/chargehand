using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Modules.Handover.Api;

/// <summary>ADR 0046: for the repositories it lists, chargehand marks a finished task's pull request ready and merges it through the repository's required checks, acting as a GitHub App
/// instead of the push credential (on GitLab, with the forge token). Unset, chargehand opens draft pull requests and never touches them again (ADR 0039).</summary>
/// <param name="AppId">The GitHub App's numeric id; <c>app_id</c> and <c>private_key_secret</c> are both set or both left out (a profile that integrates GitLab projects only needs none).</param>
/// <param name="InstallationId">Optional. An App is installed once per account or organisation, so chargehand asks GitHub which installation covers each repository's owner. This pins one installation,
/// for the account it belongs to, and skips the lookup for that owner's repositories; it reaches no other owner.</param>
/// <param name="PrivateKeySecret">The secret item holding the App's private key (PEM). Never logged, never in a container.</param>
/// <param name="Repositories">Per <c>owner/repo</c> (<c>group/subgroup/project</c> on GitLab): <c>merge</c> (ready, then merged through the required checks) or <c>ask</c> (ready, a comment, no merge). A repository not listed is off.</param>
/// <param name="AlwaysAsk">Repositories that are <c>ask</c> whatever else says: this list wins, even over <c>merge</c> and over a repository not listed.</param>
/// <param name="CiFix">The short session that fixes a failing required check; null: its defaults.</param>
public sealed partial record DrivenIntegrator(long? AppId = null, long? InstallationId = null, string? PrivateKeySecret = null, IReadOnlyDictionary<string, string>? Repositories = null,
    IReadOnlyList<string>? AlwaysAsk = null, DrivenCiFix? CiFix = null)
{
    /// <summary>Throws an invalid_request error naming the key when a value cannot work.</summary>
    public void Validate()
    {
        List<string> problems = [.. AppProblems(), .. RepositoryProblems(), .. CiFix?.Problems() ?? []];
        if (problems.Count > 0)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"driven.integrator: {string.Join("; ", problems)}", "Fix driven.integrator in the profile; docs/guide/driven.md lists the keys.");
    }

    private IEnumerable<string> AppProblems()
    {
        if (AppId is null && InstallationId is null && PrivateKeySecret is null)
            yield break;
        if (AppId is null or < 1) yield return "app_id must be the App's numeric id";
        if (InstallationId is < 1) yield return "installation_id must be the installation's numeric id";
        if (string.IsNullOrWhiteSpace(PrivateKeySecret)) yield return "private_key_secret must name a secret item";
    }

    private IEnumerable<string> RepositoryProblems()
    {
        foreach (var (name, mode) in Repositories ?? new Dictionary<string, string>())
        {
            if (!RepositoryName().IsMatch(name)) yield return $"repositories key '{name}' must be owner/repo (group/subgroup/project on GitLab)";
            if (mode is not ("merge" or "ask")) yield return $"repositories.{name} must be merge or ask";
        }
        foreach (var name in AlwaysAsk ?? [])
            if (!RepositoryName().IsMatch(name)) yield return $"always_ask entry '{name}' must be owner/repo";
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)+$")]
    private static partial Regex RepositoryName();
}

/// <summary>The CI fixer's limits (ADR 0046). A key left out keeps its default.</summary>
/// <param name="MaxAttempts">Fix sessions per pull request, 0 to 3 (default 3); 0: no fixer.</param>
/// <param name="MaxTurns">Turn limit of one fix session (default 20), in place of the preset's.</param>
/// <param name="MaxMinutes">Wall clock of one fix session (default 15).</param>
/// <param name="WaitMinutes">How long chargehand waits for the required checks of one head commit before it stops watching (default 20); the task holds its slot meanwhile.</param>
public sealed record DrivenCiFix(int? MaxAttempts = null, int? MaxTurns = null, int? MaxMinutes = null, int? WaitMinutes = null)
{
    public const int AttemptCap = 3;

    public int Attempts => MaxAttempts ?? AttemptCap;

    public int Turns => MaxTurns ?? 20;

    public int Minutes => MaxMinutes ?? 15;

    public int Wait => WaitMinutes ?? 20;

    internal IEnumerable<string> Problems()
    {
        if (MaxAttempts is < 0 or > AttemptCap) yield return $"ci_fix.max_attempts must be 0 to {AttemptCap}";
        if (MaxTurns is < 1) yield return "ci_fix.max_turns must be at least 1";
        if (MaxMinutes is < 1 or > 120) yield return "ci_fix.max_minutes must be 1 to 120";
        if (WaitMinutes is < 1 or > 240) yield return "ci_fix.wait_minutes must be 1 to 240";
    }
}
