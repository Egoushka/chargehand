using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Handover.Api;

namespace Chargehand.Modules.Intake.Api;

/// <summary>ADR 0051: work that starts without a caller. Unset, nothing is watched. The Intake module owns these settings (ADR 0060); <c>Chargehand.Automation</c> reads them until it is ported.</summary>
/// <param name="Github">Issues and comments on GitHub that start a driven batch; null: not watched.</param>
/// <param name="Schedules">Work that runs on a clock within a monthly cap (ADR 0068); null: none.</param>
/// <param name="Account">An account of <c>http.accounts</c> that every automated start (GitHub, schedules) is charged to and admitted as (ADR 0076): its caps, budget clamps, concurrency
/// limit and opt-in forecast pause apply. Null: they run as the owner, with no account caps.</param>
/// <param name="Notify">Where a driven task's question or partial stop is sent with its answer options (ADR 0077); null: nowhere, it waits in the inbox.</param>
public sealed record AutomationSettings(AutomationGitHub? Github = null, AutomationSchedules? Schedules = null, string? Account = null, AutomationNotify? Notify = null)
{
    /// <param name="drivenEnabled">Whether <c>driven.enabled</c> is set.</param>
    public void Validate(bool drivenEnabled, DrivenIntegrator? integrator)
    {
        Github?.Validate(drivenEnabled, integrator);
        Schedules?.Validate(drivenEnabled);
        Notify?.Validate(drivenEnabled);
    }
}

/// <summary>A label on an issue, or a mention of the bot in a comment, by an allowed author starts a driven batch of one task (ADR 0051). chargehand polls: the server is not reachable from GitHub.
/// It reads and comments as the GitHub App of <c>driven.integrator</c>, which needs the Issues permission (read and write) besides the ones ADR 0046 lists.</summary>
/// <param name="Repositories">The repositories watched, <c>owner/repo</c> to the server's checkout of it (under <c>repository_roots</c>). Opt-in: a repository not listed is never read, unless
/// <paramref name="Discover"/> is on.</param>
/// <param name="AllowedAuthors">Logins whose label or comment starts a batch. Required; an empty list means nothing triggers, and a comment or label from anyone else is ignored.</param>
/// <param name="Label">The label that triggers (default <c>chargehand</c>); the person who applied it must be an allowed author.</param>
/// <param name="Mention">The mention that triggers in a comment (default <c>@chargehand</c>).</param>
/// <param name="PollSeconds">How often the repositories are read (default 120).</param>
/// <param name="BudgetClass">The size class of a triggered task when the issue has no <c>size:S</c>, <c>size:M</c> or <c>size:L</c> label (default M); it sets the governor's budget (ADR 0045).</param>
/// <param name="MaxTokensTotal">The token cap of a triggered batch (default 2,000,000); binds on a subscription and on an API key.</param>
/// <param name="MaxUsdTotal">The dollar cap of a triggered batch (default 5); binds where the credential is priced.</param>
/// <param name="MaxConcurrent">Triggered batches running at once (default 2); a trigger over it waits for a slot.</param>
/// <param name="MaxStartsPerDay">Triggered batches started in any 24 hours (default 10); a trigger over it is refused.</param>
/// <param name="MaxReviewRounds">Review rounds per pull request (default 3, 0 to turn the loop off): comments of allowed authors on a chargehand pull request start a session on its branch. The CI fixer's attempts count
/// against the same cap of 3 per pull request, so rounds and fix attempts together never pass it (ADR 0051).</param>
/// <param name="ReviewPulls">Review every open, non-draft pull request of the watched repositories once per head commit and comment the findings (default false).</param>
/// <param name="MaxReviewsPerDay">Reviews started in any 24 hours (default 20); a pull request over it waits until the window has room.</param>
/// <param name="Discover">Also watch every repository the App's installations can see, each cloned into <c>&lt;worker_root&gt;/repos/&lt;owner&gt;/&lt;repo&gt;</c> and fetched with the App's token (default false).
/// A repository in <paramref name="Repositories"/> keeps its own checkout.</param>
public sealed partial record AutomationGitHub(IReadOnlyDictionary<string, AutomationRepository>? Repositories = null, IReadOnlyList<string>? AllowedAuthors = null, string Label = "chargehand",
    string Mention = "@chargehand", int PollSeconds = 120, string BudgetClass = "M", long MaxTokensTotal = 2_000_000, decimal MaxUsdTotal = 5m, int MaxConcurrent = 2, int MaxStartsPerDay = 10, int MaxReviewRounds = 3,
    bool ReviewPulls = false, int MaxReviewsPerDay = 20, bool Discover = false)
{
    /// <summary>Where discovered repositories are cloned, under <c>worker_root</c>.</summary>
    public const string ClonesDirectory = "repos";

    public const int MinPollSeconds = 30;

    internal void Validate(bool drivenEnabled, DrivenIntegrator? integrator)
    {
        List<string> problems = [.. SourceProblems(drivenEnabled, integrator), .. TriggerProblems(), .. LimitProblems()];
        if (problems.Count > 0)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"automation.github: {string.Join("; ", problems)}", "Fix automation.github in the profile; docs/guide/intake.md lists the keys.");
    }

    private IEnumerable<string> SourceProblems(bool drivenEnabled, DrivenIntegrator? integrator)
    {
        if (!drivenEnabled || integrator is not { AppId: not null, PrivateKeySecret: not null })
            yield return "needs driven.enabled and the GitHub App of driven.integrator (app_id, private_key_secret), which reads and comments";
        if (Repositories is not { Count: > 0 } && !Discover)
            yield return "repositories must list at least one owner/repo, or discover must be true";
        foreach (var (name, repository) in Repositories ?? new Dictionary<string, AutomationRepository>())
        {
            if (!RepositoryName().IsMatch(name)) yield return $"repositories key '{name}' must be owner/repo";
            if (string.IsNullOrWhiteSpace(repository?.Path)) yield return $"repositories.{name}.path must be the server's checkout of the repository";
        }
        if (AllowedAuthors is null)
            yield return "allowed_authors is required (an empty list means nothing triggers)";
        foreach (var login in AllowedAuthors ?? [])
            if (!Login().IsMatch(login)) yield return $"allowed_authors entry '{login}' must be a GitHub login";
    }

    private IEnumerable<string> TriggerProblems()
    {
        if (!LabelName().IsMatch(Label)) yield return "label must be a label name without commas";
        if (!MentionName().IsMatch(Mention)) yield return "mention must be @ and a GitHub login";
        if (PollSeconds is < MinPollSeconds or > 3600) yield return $"poll_seconds must be {MinPollSeconds} to 3600";
    }

    private IEnumerable<string> LimitProblems()
    {
        if (BudgetClass is not ("S" or "M" or "L")) yield return "budget_class must be S, M or L";
        if (MaxTokensTotal < 1) yield return "max_tokens_total must be above 0";
        if (MaxUsdTotal <= 0) yield return "max_usd_total must be above 0";
        if (MaxConcurrent is < 1 or > 10) yield return "max_concurrent must be 1 to 10";
        if (MaxStartsPerDay is < 1 or > 1000) yield return "max_starts_per_day must be 1 to 1000";
        if (MaxReviewsPerDay is < 1 or > 1000) yield return "max_reviews_per_day must be 1 to 1000";
        if (MaxReviewRounds is < 0 or > DrivenCiFix.AttemptCap) yield return $"max_review_rounds must be 0 to {DrivenCiFix.AttemptCap}";
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryName();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})(?:\[bot\])?$")]
    private static partial Regex Login();

    [GeneratedRegex(@"^[^,\s][^,]{0,49}$")]
    private static partial Regex LabelName();

    [GeneratedRegex(@"^@[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    private static partial Regex MentionName();
}

/// <param name="Path">The checkout the server reads the base commit from and the batch works from (ADR 0023); its <c>origin</c> is the repository.</param>
public sealed record AutomationRepository(string Path);
