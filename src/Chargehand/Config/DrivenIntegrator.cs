using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Config;

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
    internal void Validate()
    {
        var problems = new List<string>();
        if (AppId is not null || InstallationId is not null || PrivateKeySecret is not null)
        {
            if (AppId is null or < 1) problems.Add("app_id must be the App's numeric id");
            if (InstallationId is < 1) problems.Add("installation_id must be the installation's numeric id");
            if (string.IsNullOrWhiteSpace(PrivateKeySecret)) problems.Add("private_key_secret must name a secret item");
        }
        foreach (var (name, mode) in Repositories ?? new Dictionary<string, string>())
        {
            if (!RepositoryName().IsMatch(name)) problems.Add($"repositories key '{name}' must be owner/repo (group/subgroup/project on GitLab)");
            if (mode is not ("merge" or "ask")) problems.Add($"repositories.{name} must be merge or ask");
        }
        foreach (var name in AlwaysAsk ?? [])
            if (!RepositoryName().IsMatch(name)) problems.Add($"always_ask entry '{name}' must be owner/repo");
        problems.AddRange(CiFix?.Problems() ?? []);
        if (problems.Count > 0)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"driven.integrator: {string.Join("; ", problems)}", "Fix driven.integrator in the profile; docs/guide/driven.md lists the keys.");
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

/// <summary>Which code host a remote is on, when its host name does not say (ADR 0047's forge point). Unset, a remote on <c>gitlab.com</c> or a host whose first label is <c>gitlab</c> is GitLab and any other
/// host is GitHub, as before.</summary>
/// <param name="Kind"><c>github</c> or <c>gitlab</c>, for every remote of the profile.</param>
/// <param name="BaseUrl">The API root: <c>https://host/api/v4</c> for GitLab (the instance root <c>https://host</c> is accepted), <c>https://host/api/v3</c> for GitHub Enterprise. Unset: derived from the remote's host (GitLab) or api.github.com.</param>
/// <param name="TokenSecret">GitLab only: the secret item for the API token (<c>PRIVATE-TOKEN</c>, scope <c>api</c>). Unset: <c>driven.push_secret</c>, which then needs <c>api</c> and <c>write_repository</c>.</param>
public sealed record DrivenForge(string Kind, string? BaseUrl = null, string? TokenSecret = null)
{
    /// <summary>Throws an invalid_request error naming the key when a value cannot work.</summary>
    internal void Validate()
    {
        var problems = new List<string>();
        if (Kind is not ("github" or "gitlab")) problems.Add("kind must be github or gitlab");
        if (BaseUrl is not null && !(Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0))
            problems.Add("base_url must be an https URL without credentials, query or fragment");
        if (TokenSecret is { } secret && string.IsNullOrWhiteSpace(secret)) problems.Add("token_secret must name a secret item");
        if (TokenSecret is not null && Kind == "github") problems.Add("token_secret is for gitlab; GitHub uses driven.push_secret and the App");
        if (problems.Count > 0)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"driven.forge: {string.Join("; ", problems)}", "Fix driven.forge in the profile; docs/guide/driven.md lists the keys.");
    }
}
