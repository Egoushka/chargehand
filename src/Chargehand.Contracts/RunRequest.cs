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
/// <param name="Plan">Run the planning step before the batch (ADR 0049); null: the profile's <c>driven.planning.enabled</c> decides.</param>
public sealed record RequestDriven(
    IReadOnlyList<DrivenTask> Tasks,
    int? MaxParallel = null,
    long? MaxTokensTotal = null,
    decimal? MaxUsdTotal = null,
    bool DraftPr = true,
    bool? Plan = null);

/// <param name="MaxTurns">Replaces the preset's <c>max_turns</c> for this task's sessions; null: the preset's.</param>
/// <param name="MaxMinutes">Replaces the preset's <c>max_minutes</c> for this task's sessions; null: the preset's.</param>
/// <param name="Size">S, M or L: the size class whose budget the governor holds the task to (ADR 0045); null: M.</param>
/// <param name="BudgetUsd">The task's own budget in the governor's dollar equivalent, in place of its size class's.</param>
/// <param name="Answers">Answers to the questions an earlier session of the task named by <paramref name="ContinueFrom"/> asked; they go into the goal verbatim (ADR 0045).</param>
/// <param name="ContinueFrom">An earlier batch's task that ended <c>needs_input</c> or stopped with work kept: this task starts from that task's branch and takes its goal, so it needs neither <c>goal</c> nor <c>ref</c>.</param>
/// <param name="DependsOn">Ids of tasks of this batch that must end <c>completed</c> first; the task starts from their branches (ADR 0049). Null or empty: independent.</param>
/// <param name="Title">The title of the task's pull request, one line; null: the goal's first sentence, cut to 72 characters. The pull request body holds the whole goal either way.</param>
/// <param name="Acceptance">Commands the session's branch must pass before the task counts as done; each is run in the session's working tree and its failure is sent back to the session.</param>
public sealed record DrivenTask(string Id, string? Ref = null, string? Goal = null, int? MaxTurns = null, int? MaxMinutes = null, string? Size = null, decimal? BudgetUsd = null,
    IReadOnlyList<string>? Answers = null, ContinueFrom? ContinueFrom = null, IReadOnlyList<string>? DependsOn = null, string? Title = null,
    IReadOnlyList<AcceptanceCheck>? Acceptance = null);

/// <summary>A machine-checkable acceptance criterion: a shell command, the exit code it must end with and, when set, text its output must contain.</summary>
public sealed record AcceptanceCheck(string Command, int ExitCode = 0, string? Output = null);

/// <param name="RunId">The earlier batch's run id.</param>
/// <param name="TaskId">The task's id in that batch.</param>
public sealed record ContinueFrom(string RunId, string TaskId);

/// <summary>The request rules JSON Schema cannot express.</summary>
public static class DrivenRules
{
    public static IReadOnlyList<string> Problems(RequestDriven driven) =>
        driven.Tasks.GroupBy(t => t.Id).Where(g => g.Count() > 1).Select(g => $"task id '{g.Key}' is used more than once")
            .Concat(driven.Tasks.Where(t => t.MaxTurns is < 1 or > MaxTurnsCeiling).Select(t => $"task '{t.Id}': max_turns must be 1 to {MaxTurnsCeiling}"))
            .Concat(driven.Tasks.Where(t => t.MaxMinutes is < 1 or > MaxMinutesCeiling).Select(t => $"task '{t.Id}': max_minutes must be 1 to {MaxMinutesCeiling}"))
            .Concat(driven.Tasks.Where(t => t.Size is not (null or "S" or "M" or "L")).Select(t => $"task '{t.Id}': size must be S, M or L"))
            .Concat(driven.Tasks.Where(t => t.BudgetUsd is <= 0).Select(t => $"task '{t.Id}': budget_usd must be above 0"))
            .Concat(driven.Tasks.Where(t => t.Title is not null && (string.IsNullOrWhiteSpace(t.Title) || t.Title.Length > MaxTitleLength || t.Title.AsSpan().ContainsAny('\r', '\n')))
                .Select(t => $"task '{t.Id}': title must be one line of 1 to {MaxTitleLength} characters"))
            .Concat(driven.Tasks.Where(t => t.Acceptance is { Count: > MaxAcceptance }).Select(t => $"task '{t.Id}': at most {MaxAcceptance} acceptance checks"))
            .Concat(driven.Tasks.Where(t => t.Acceptance?.Any(a => string.IsNullOrWhiteSpace(a.Command)) == true).Select(t => $"task '{t.Id}': an acceptance check needs a command"))
            .Concat(driven.Tasks.Where(t => t.Answers is not null && t.ContinueFrom is null).Select(t => $"task '{t.Id}': answers need continue_from"))
            .Concat(driven.Tasks.Where(t => t.ContinueFrom is not null && (t.Goal is not null || t.Ref is not null)).Select(t => $"task '{t.Id}': continue_from takes the earlier task's goal; leave out goal and ref"))
            .Concat(driven.Tasks.Where(t => t.Answers?.Any(string.IsNullOrWhiteSpace) == true).Select(t => $"task '{t.Id}': answers must not be empty"))
            .Concat(driven.Tasks.Where(t => t.ContinueFrom is { } from && (!RunIdPattern.IsMatch(from.RunId) || !RunIdPattern.IsMatch(from.TaskId)))
                .Select(t => $"task '{t.Id}': continue_from needs a run id and a task id"))
            .Concat(driven.Tasks.Where(t => t.ContinueFrom is not null).GroupBy(t => t.ContinueFrom!).Where(g => g.Count() > 1)
                .Select(g => $"tasks {string.Join(", ", g.Select(t => $"'{t.Id}'"))} all continue {g.Key.RunId}/{g.Key.TaskId}: they would share its branch"))
            .Concat(DependencyProblems(driven.Tasks)).ToList();

    /// <summary>The <c>depends_on</c> rules JSON Schema cannot express: every id is a task of the batch, none repeats or names its own task, no task continues an earlier one
    /// and also depends on another (two different starting points), and the graph has no cycle.</summary>
    public static IReadOnlyList<string> DependencyProblems(IReadOnlyList<DrivenTask> tasks)
    {
        List<string> problems = [];
        var ids = tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var task in tasks.Where(t => t.DependsOn is { Count: > 0 }))
        {
            problems.AddRange(task.DependsOn!.Where(d => d == task.Id).Select(_ => $"task '{task.Id}': depends_on names the task itself"));
            problems.AddRange(task.DependsOn!.Where(d => d != task.Id && !ids.Contains(d)).Distinct().Select(d => $"task '{task.Id}': depends_on names '{d}', which is not a task of this batch"));
            problems.AddRange(task.DependsOn!.GroupBy(d => d).Where(g => g.Count() > 1).Select(g => $"task '{task.Id}': depends_on names '{g.Key}' more than once"));
            if (task.ContinueFrom is not null)
                problems.Add($"task '{task.Id}': depends_on and continue_from start from different work; use one");
        }
        if (problems.Count > 0)
            return problems;
        // A repeated id is reported on its own; the first of them stands in for the graph here.
        var graph = tasks.GroupBy(t => t.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().DependsOn ?? [], StringComparer.Ordinal);
        if (Cycle(graph) is { Count: > 0 } cycle)
            problems.Add($"tasks depend on each other in a cycle: {string.Join(" -> ", cycle)}");
        return problems;
    }

    /// <summary>The ids of one dependency cycle in the order they depend on each other (the first id repeated at the end), or empty when the graph has none.</summary>
    public static IReadOnlyList<string> Cycle(IReadOnlyDictionary<string, IReadOnlyList<string>> dependsOn)
    {
        Dictionary<string, int> mark = new(StringComparer.Ordinal);
        List<string> path = [];
        IReadOnlyList<string> Visit(string id)
        {
            mark[id] = 1;
            path.Add(id);
            foreach (var next in dependsOn.GetValueOrDefault(id) ?? [])
            {
                if (!dependsOn.ContainsKey(next))
                    continue;
                if (mark.GetValueOrDefault(next) == 1)
                    return [.. path.SkipWhile(p => p != next), next];
                if (mark.GetValueOrDefault(next) == 0 && Visit(next) is { Count: > 0 } found)
                    return found;
            }
            path.RemoveAt(path.Count - 1);
            mark[id] = 2;
            return [];
        }
        foreach (var id in dependsOn.Keys.Where(id => mark.GetValueOrDefault(id) == 0))
            if (Visit(id) is { Count: > 0 } found)
                return found;
        return [];
    }

    private static readonly System.Text.RegularExpressions.Regex RunIdPattern = new("^[a-z0-9][a-z0-9_.-]{0,63}$");

    public const int MaxTurnsCeiling = 1000;

    public const int MaxMinutesCeiling = 480;

    public const int MaxTitleLength = 200;

    public const int MaxAcceptance = 20;

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
