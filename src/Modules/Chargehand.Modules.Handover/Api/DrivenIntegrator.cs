using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Chargehand.Kernel;
using Microsoft.Extensions.Options;

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
/// <param name="AppSlug">The App's slug, its login without <c>[bot]</c> (GitHub shows it on the App's settings page). Required alongside <paramref name="AppId"/>: it is what chargehand commits as
/// (<see cref="CommitIdentity"/>), since GitHub never hands the slug back from <c>app_id</c> alone.</param>
public sealed partial record DrivenIntegrator(long? AppId = null, long? InstallationId = null, string? PrivateKeySecret = null, IReadOnlyDictionary<string, string>? Repositories = null,
    IReadOnlyList<string>? AlwaysAsk = null, DrivenCiFix? CiFix = null, string? AppSlug = null) : IValidatableObject
{
    /// <summary>The default identity, when no GitHub App is configured.</summary>
    public static readonly (string Name, string Email) DefaultIdentity = ("chargehand", "chargehand@localhost");

    /// <summary>What chargehand commits as (the secrets/noreply CI check some repositories run rejects <see cref="DefaultIdentity"/>'s <c>@localhost</c> address): the App's bot,
    /// <c>&lt;slug&gt;[bot] &lt;&lt;slug&gt;[bot]@users.noreply.github.com&gt;</c>, once an App is configured (<see cref="AppSlug"/> is then required); <see cref="DefaultIdentity"/> otherwise.</summary>
    public static (string Name, string Email) CommitIdentity(DrivenIntegrator? integrator) =>
        integrator?.AppSlug is { } slug ? ($"{slug}[bot]", $"{slug}[bot]@users.noreply.github.com") : DefaultIdentity;

    /// <summary>Throws an invalid_request error naming the key when a value cannot work.</summary>
    public void Validate() => SettingsValidation.ThrowIfFailed(new DrivenIntegratorValidator().Validate(null, this), "driven.integrator",
        "Fix driven.integrator in the profile; docs/guide/driven.md lists the keys.");

    IEnumerable<ValidationResult> IValidatableObject.Validate(ValidationContext validationContext) =>
        SettingsValidation.Results([.. AppProblems(), .. RepositoryProblems(), .. CiFix?.Problems() ?? []]);

    private IEnumerable<string> AppProblems()
    {
        if (AppId is null && InstallationId is null && PrivateKeySecret is null && AppSlug is null)
            yield break;
        if (AppId is null or < 1) yield return "app_id must be the App's numeric id";
        if (InstallationId is < 1) yield return "installation_id must be the installation's numeric id";
        if (string.IsNullOrWhiteSpace(PrivateKeySecret)) yield return "private_key_secret must name a secret item";
        if (string.IsNullOrWhiteSpace(AppSlug)) yield return "app_slug must be the App's slug (its login, without [bot])";
        else if (!Slug().IsMatch(AppSlug)) yield return "app_slug must be letters, digits and hyphens, as GitHub shows it";
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

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    private static partial Regex Slug();
}

/// <summary><c>driven.integrator</c>'s validator, source-generated (ADR 0060).</summary>
[OptionsValidator]
public sealed partial class DrivenIntegratorValidator : IValidateOptions<DrivenIntegrator>;

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
