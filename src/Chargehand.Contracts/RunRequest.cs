namespace Chargehand.Contracts;

/// <summary>request/v1: what a caller sends to start a run.</summary>
public sealed record RunRequest(
    string ContractVersion,
    string Text,
    RequestContext Context,
    IReadOnlyList<CallerInput>? Inputs = null,
    IReadOnlyList<PromptBlock>? CallerBlocks = null,
    RequestDriven? Driven = null);

/// <param name="Approved">Runs a request the preset would otherwise stop for approval (preset approval thresholds).</param>
public sealed record RequestContext(bool Interactive, string Preset, decimal? BudgetUsd = null, RepositoryRef? Repository = null, bool? Approved = null, IReadOnlyList<string>? Verify = null);

public sealed record RepositoryRef(string Path, string Commit);

/// <summary>A caller-supplied fact; claims cite it with evidence kind <c>input</c>.</summary>
public sealed record CallerInput(string Id, string Kind, string Text, string? SourceUrl = null);

/// <summary>A versioned, content-hashed prompt block (registry-owned or caller-owned).</summary>
public sealed record PromptBlock(string Name, string Version, string Sha256, string Text)
{
    /// <summary>A block whose sha256 follows request/v1's rule, so a caller needs nothing else to send one.</summary>
    public static PromptBlock Create(string name, string version, string text) => new(name, version, Hash(text), text);

    /// <summary>LF line endings, trailing whitespace stripped from every line.</summary>
    public static string Normalize(string text) =>
        string.Join('\n', text.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd()));

    /// <summary>sha256 of the normalised UTF-8 text, lowercase hex.</summary>
    public static string Hash(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Normalize(text))));
}

/// <summary>A batch of tasks for driven sessions (ADR 0039). <c>Ref</c> is a tracker item id, resolved through the profile's task
/// source; <c>Goal</c> is free text. Each task needs one of them. <c>DraftPr</c> is always true: the field exists so a non-draft mode
/// would be a visible change.</summary>
/// <param name="MaxTokensTotal">Input plus output tokens over the whole batch, counted from each session's stream; binds in both credential modes.</param>
/// <param name="MaxUsdTotal">Dollars over the batch; enforced only where the credential is priced (an API key).</param>
public sealed record RequestDriven(
    IReadOnlyList<DrivenTask> Tasks,
    int? MaxParallel = null,
    long? MaxTokensTotal = null,
    decimal? MaxUsdTotal = null,
    bool DraftPr = true);

/// <param name="MaxTurns">Replaces the preset's <c>max_turns</c> for this task's sessions; null: the preset's.</param>
/// <param name="MaxMinutes">Replaces the preset's <c>max_minutes</c> for this task's sessions; null: the preset's.</param>
/// <param name="Size">S, M or L: the size class whose budget the governor holds the task to (ADR 0045); null: M.</param>
/// <param name="BudgetUsd">The task's own budget in the governor's dollar equivalent, in place of its size class's.</param>
public sealed record DrivenTask(string Id, string? Ref = null, string? Goal = null, int? MaxTurns = null, int? MaxMinutes = null, string? Size = null, decimal? BudgetUsd = null);

/// <summary>The request rules JSON Schema cannot express.</summary>
public static class DrivenRules
{
    public static IReadOnlyList<string> Problems(RequestDriven driven) =>
        driven.Tasks.GroupBy(t => t.Id).Where(g => g.Count() > 1).Select(g => $"task id '{g.Key}' is used more than once")
            .Concat(driven.Tasks.Where(t => t.MaxTurns is < 1 or > MaxTurnsCeiling).Select(t => $"task '{t.Id}': max_turns must be 1 to {MaxTurnsCeiling}"))
            .Concat(driven.Tasks.Where(t => t.MaxMinutes is < 1 or > MaxMinutesCeiling).Select(t => $"task '{t.Id}': max_minutes must be 1 to {MaxMinutesCeiling}"))
            .Concat(driven.Tasks.Where(t => t.Size is not (null or "S" or "M" or "L")).Select(t => $"task '{t.Id}': size must be S, M or L"))
            .Concat(driven.Tasks.Where(t => t.BudgetUsd is <= 0).Select(t => $"task '{t.Id}': budget_usd must be above 0")).ToList();

    public const int MaxTurnsCeiling = 1000;

    public const int MaxMinutesCeiling = 480;

    /// <summary>All problems of a request that carries <c>driven</c>: duplicate task ids and a missing <c>context.repository</c>.</summary>
    public static IReadOnlyList<string> Problems(RunRequest request)
    {
        if (request.Driven is null)
            return [];
        var problems = Problems(request.Driven).ToList();
        if (request.Context.Repository is null)
            problems.Add("a driven request needs context.repository");
        return problems;
    }
}
