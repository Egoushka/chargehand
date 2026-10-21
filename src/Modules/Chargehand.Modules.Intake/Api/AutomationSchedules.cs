using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Modules.Intake.Api;

/// <summary>ADR 0068: recurring work on a schedule, within a monthly cap. Unset, nothing runs on a clock.</summary>
/// <param name="Entries">What runs and when.</param>
/// <param name="NotifyUrl">An ntfy-style URL: the result of a read-only entry and the morning report are POSTed to it as plain text with a <c>Title</c> header. Required by <c>morning-report</c>; the URL can carry a secret topic, so it is never logged.</param>
public sealed partial record AutomationSchedules(IReadOnlyList<ScheduleEntry>? Entries = null, string? NotifyUrl = null)
{
    public const int MaxGoal = 4000;

    internal void Validate(bool drivenEnabled)
    {
        List<string> problems = [.. Problems(drivenEnabled)];
        if (problems.Count > 0)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"automation.schedules: {string.Join("; ", problems)}", "Fix automation.schedules in the profile; docs/guide/intake.md links the guide that lists the keys (docs/guide/schedules.md).");
    }

    private IEnumerable<string> Problems(bool drivenEnabled)
    {
        if (NotifyUrl is not null && !(Uri.TryCreate(NotifyUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"))
            yield return "notify_url must be an http or https URL";
        if (Entries is not { Count: > 0 })
            yield return "entries must list at least one schedule";
        foreach (var duplicate in (Entries ?? []).GroupBy(e => e?.Id).Where(g => g.Count() > 1))
            yield return $"id '{duplicate.Key}' is used more than once";
        foreach (var entry in Entries ?? [])
            foreach (var problem in entry is null ? ["an entry is empty"] : entry.Problems(drivenEnabled, NotifyUrl is not null))
                yield return problem;
    }

    internal static bool IsSlug(string? id) => id is not null && Slug().IsMatch(id);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex Slug();
}

/// <summary>One recurring piece of work. Exactly one of <paramref name="Template"/> and <paramref name="Goal"/>.</summary>
/// <param name="Id">A slug (lowercase letters, digits, hyphens); names the entry in the state file, the logs and the notification title.</param>
/// <param name="At"><c>daily HH:MM</c>, <c>weekly mon..sun HH:MM</c> or <c>monthly 1-28 HH:MM</c>, in UTC (<see cref="ScheduleAt"/>).</param>
/// <param name="Template">A built-in (<see cref="ScheduleTemplates"/>).</param>
/// <param name="Goal">Free text. Read-only (the <c>default</c> preset answers it) unless <paramref name="Driven"/>.</param>
/// <param name="Driven">With <paramref name="Goal"/> only: run it as a driven batch of one task that may write and open a pull request.</param>
/// <param name="Repository">The checkout (under <c>repository_roots</c>) the work reads or changes; not used by <c>morning-report</c>.</param>
/// <param name="MaxPerMonth">Starts per calendar month (UTC), 1 to 100. Default 4; 31 for <c>morning-report</c>. An occurrence over it is skipped and logged.</param>
/// <param name="BudgetClass">S, M or L for a driven entry (default S): the governor's budget (ADR 0045).</param>
public sealed record ScheduleEntry(string Id, string At, string? Template = null, string? Goal = null, bool Driven = false, AutomationRepository? Repository = null, int? MaxPerMonth = null,
    string? BudgetClass = null)
{
    public const int DefaultMaxPerMonth = 4;
    public const int ReportMaxPerMonth = 31;

    /// <summary>How the entry runs; read-only for an entry that validation would refuse.</summary>
    public ScheduleKind Kind() => ScheduleTemplates.Find(Template)?.Kind ?? (Driven ? ScheduleKind.Driven : ScheduleKind.ReadOnly);

    /// <summary>The text the worker gets; null for the report.</summary>
    public string? Text() => ScheduleTemplates.Find(Template) is { } template ? template.Goal : Goal;

    public int Cap() => MaxPerMonth ?? (Template == ScheduleTemplates.MorningReport ? ReportMaxPerMonth : DefaultMaxPerMonth);

    public string Size() => BudgetClass ?? "S";

    public ScheduleAt Schedule() => ScheduleAt.TryParse(At, out var at, out _) ? at : default;

    internal IEnumerable<string> Problems(bool drivenEnabled, bool hasNotifyUrl) => [.. WhatProblems(), .. LimitProblems(), .. KindProblems(drivenEnabled, hasNotifyUrl)];

    private string Name => $"schedule '{Id}'";

    private IEnumerable<string> WhatProblems()
    {
        if (!AutomationSchedules.IsSlug(Id)) yield return $"id '{Id}' must be lowercase letters, digits and hyphens, 1 to 40 characters";
        if (!ScheduleAt.TryParse(At, out _, out var atProblem)) yield return $"{Name}: at {atProblem}";
        if ((Template is null) == (Goal is null)) yield return $"{Name}: set exactly one of template and goal";
        if (Template is not null && ScheduleTemplates.Find(Template) is null) yield return $"{Name}: template '{Template}' must be one of {string.Join(", ", ScheduleTemplates.Names)}";
        if (Goal is not null && (string.IsNullOrWhiteSpace(Goal) || Goal.Length > AutomationSchedules.MaxGoal)) yield return $"{Name}: goal must be 1 to {AutomationSchedules.MaxGoal} characters";
        if (Driven && Goal is null) yield return $"{Name}: driven applies to a goal; a template says itself whether it writes";
    }

    private IEnumerable<string> LimitProblems()
    {
        if (MaxPerMonth is < 1 or > 100) yield return $"{Name}: max_per_month must be 1 to 100";
        if (BudgetClass is not (null or "S" or "M" or "L")) yield return $"{Name}: budget_class must be S, M or L";
    }

    private IEnumerable<string> KindProblems(bool drivenEnabled, bool hasNotifyUrl)
    {
        var kind = Kind();
        if (kind == ScheduleKind.Driven && !drivenEnabled) yield return $"{Name}: a driven entry needs driven.enabled";
        if (kind == ScheduleKind.Report && !hasNotifyUrl) yield return $"{Name}: morning-report needs notify_url";
        if (kind != ScheduleKind.Report && string.IsNullOrWhiteSpace(Repository?.Path)) yield return $"{Name}: repository.path must be the server's checkout of the repository";
    }
}
