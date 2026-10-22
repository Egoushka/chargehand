using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Chargehand.Modules.Forge.Api;

namespace Chargehand.Modules.Oversight.Api;

/// <summary>The words a record uses for how a task ended.</summary>
public static class LearningOutcome
{
    public const string Completed = "completed";
    public const string Partial = "partial";
    public const string Failed = "failed";
    public const string NeedsInput = "needs_input";
}

/// <summary>What a person did with a task's pull request (ADR 0045): <see cref="MergedUnchanged"/> when the head at the merge is the commit chargehand last left on the branch,
/// <see cref="MergedEdited"/> when it is another, <see cref="Merged"/> when the record kept no commit to compare.</summary>
public static class PrOutcomes
{
    public const string MergedUnchanged = "merged_unchanged";
    public const string MergedEdited = "merged_edited";
    public const string Merged = "merged";
    public const string Closed = "closed";
    public const string Open = "open";

    public static bool Resolved(string outcome) => outcome != Open;

    /// <param name="pushedHead">The last commit chargehand put on the branch: its handover, a CI fix, or a branch update it made.</param>
    public static string Classify(PrSnapshot pr, string? pushedHead) =>
        pr.Merged ? pushedHead is null ? Merged : string.Equals(pr.HeadSha, pushedHead, StringComparison.OrdinalIgnoreCase) ? MergedUnchanged : MergedEdited
        : pr.Closed ? Closed : Open;
}

/// <summary>Where a task's budget came from (ADR 0045): <c>task</c> (the request's <c>budget_usd</c>), <c>flat</c> (the profile's or the built-in budget of its size class),
/// <c>history</c> (a percentile of past spend: <paramref name="N"/> tasks, the <paramref name="Percentile"/>th, for <paramref name="Scope"/>), or <c>none</c> (no governor).</summary>
/// <param name="Scope"><c>repository</c> or <c>all</c> for <c>history</c>.</param>
public sealed record BudgetSource(string Kind, int? N = null, int? Percentile = null, string? Scope = null);

/// <param name="Usd">In the dollar equivalent of <see cref="LearningSpend.UsdEstimate"/>; null: no governor, so no budget.</param>
public sealed record LearningBudget(decimal? Usd, BudgetSource Source);

/// <param name="UsdEstimate">The task's dollar equivalent from its per-message tokens, the CI fix sessions' included; null when none could be priced.</param>
/// <param name="Continuations">Sessions started after the first because one was stopped (ADR 0044).</param>
public sealed record LearningSpend(decimal? UsdEstimate, bool UsdEstimatePartial, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, int Turns, int Continuations);

/// <summary>How many times the governor decided <paramref name="Action"/> on <paramref name="Signal"/> in the task, and how many of those ended a session (<c>acted</c>).</summary>
public sealed record GovernorCount(string Signal, string Action, int Count, int Acted);

/// <param name="HeadSha">The last commit chargehand put on the branch, to compare with the head at the merge.</param>
public sealed record LearningPr(string Url, int Number, RemoteRepository Remote, string? HeadSha);

/// <summary>What one finished driven task teaches (ADR 0045): written when the task ends, next to the run log, by the server. It holds no goal, prompt or session text: counts, sizes, money and
/// reasons. A task that threw before its session ended (cancelled, a start that failed) writes none.</summary>
/// <param name="Repository">Where the task ran, for grouping: <c>owner/name</c> of the remote, else the request's repository path.</param>
/// <param name="SizeClass">S, M or L; M when the request named none and no plan did.</param>
/// <param name="SizeFrom"><c>caller</c> (the request's), <c>plan</c> (the planning step's estimate) or <c>default</c>.</param>
/// <param name="Outcome">A <see cref="LearningOutcome"/>.</param>
/// <param name="StopReason">How the last session ended: <c>completed</c>, <c>turn_limit</c>, <c>checkpoint_spend</c>, <c>budget_spent</c>, ...</param>
/// <param name="GovernorMode"><c>off</c>, <c>observe</c> or <c>enforce</c>; null: no governor in the sessions.</param>
/// <param name="Actor">The account that started the batch (ADR 0050); null: the owner, or a batch from before accounts. An account's <c>GET /v1/learn</c> sees only its own.</param>
/// <param name="MemoryPeakMb">The most memory any of the task's session containers used (MiB), read from its cgroup while it ran (ADR 0045, admission); null: not measured (a record from before it, or an engine that could not).</param>
/// <param name="Bench">The batch was a benchmark run (<c>driven.bench</c>): its tasks are built to fail or run against stand-in forges, so the store leaves them out of budgets, reports and the console unless asked. Written only when true.</param>
public sealed record LearningRecord(string BatchId, string TaskId, string RunId, DateTimeOffset At, string Repository, string SizeClass, string SizeFrom, LearningBudget Budget,
    LearningSpend Spend, string Outcome, string? StopReason, string? GovernorMode, IReadOnlyList<GovernorCount> Governor, bool TestIntegrityFlagged, LearningPr? Pr = null, string? Actor = null,
    int? MemoryPeakMb = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Bench = false)
{
    /// <summary>The task had at least one checkpoint decision, acted on or only logged.</summary>
    public bool Checkpointed => Governor.Any(g => g.Action == "checkpoint" && g.Count > 0);

    public bool Completed => Outcome == LearningOutcome.Completed;
}

/// <summary>A pull request's outcome, read back from the forge; the latest line for a task wins.</summary>
public sealed record PrOutcomeRecord(string RunId, DateTimeOffset At, string Outcome, string? HeadSha);

/// <summary>A task's record with the latest outcome of its pull request, if one was read, and its failure autopsy, if one was written.</summary>
public sealed record LearningEntry(LearningRecord Record, PrOutcomeRecord? PrOutcome, AutopsyRecord? Autopsy = null);

/// <summary>The learning records of driven tasks (ADR 0045). The server owns the file; the CLI's <c>learn report</c> only reads it.</summary>
public interface ILearningStore
{
    Task AppendAsync(LearningRecord record, CancellationToken ct);

    Task AppendAsync(PrOutcomeRecord record, CancellationToken ct);

    Task AppendAsync(AutopsyRecord record, CancellationToken ct);

    /// <summary>One entry per task attempt (the latest record of a batch, task and run id), oldest first. Benchmark tasks are left out unless <paramref name="includeBench"/>.</summary>
    Task<IReadOnlyList<LearningEntry>> ReadAsync(CancellationToken ct, bool includeBench = false);
}

/// <summary>One JSON object per line, tagged <c>task</c> or <c>pr_outcome</c>, append-only. Only the server process appends, so a gate in the process is enough; a reader skips a last line that has no newline
/// yet, and a line it cannot read. A task attempt is one (batch, task, run id): appending a record that says what the file's latest one for it already says writes nothing, a changed one supersedes it,
/// and a reader takes the latest, so a file that holds the same attempt twice reads as once.</summary>
public sealed class JsonlLearningStore(string path) : ILearningStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task AppendAsync(LearningRecord record, CancellationToken ct)
    {
        var node = JsonSerializer.SerializeToNode(record, LearningJsonContext.Default.LearningRecord)!;
        await Gate.WaitAsync(ct);
        try
        {
            if (Parse(await Text(ct)).Tasks.TryGetValue(KeyOf(record), out var held) && Same(held, record))
                return;
            await WriteLine("task", node, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task AppendAsync(PrOutcomeRecord record, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            await WriteLine("pr_outcome", JsonSerializer.SerializeToNode(record, LearningJsonContext.Default.PrOutcomeRecord)!, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task AppendAsync(AutopsyRecord record, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            await WriteLine("autopsy", JsonSerializer.SerializeToNode(record, LearningJsonContext.Default.AutopsyRecord)!, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<IReadOnlyList<LearningEntry>> ReadAsync(CancellationToken ct, bool includeBench = false)
    {
        var (tasks, outcomes, autopsies) = Parse(await Text(ct));
        return [.. tasks.Values.Where(t => includeBench || !t.Bench).OrderBy(t => t.At)
            .Select(t => new LearningEntry(t, outcomes.GetValueOrDefault(t.RunId), autopsies.GetValueOrDefault(t.RunId)))];
    }

    private static (string Batch, string Task, string Run) KeyOf(LearningRecord record) => (record.BatchId, record.TaskId, record.RunId);

    /// <summary>Whether two records of one attempt say the same: all but when they were written.</summary>
    private static bool Same(LearningRecord a, LearningRecord b) =>
        JsonSerializer.Serialize(a with { At = default }, LearningJsonContext.Default.LearningRecord) == JsonSerializer.Serialize(b with { At = default }, LearningJsonContext.Default.LearningRecord);

    private async Task<string> Text(CancellationToken ct)
    {
        if (!File.Exists(path))
            return "";
        var text = await File.ReadAllTextAsync(path, ct);
        return text[..(text.LastIndexOf('\n') + 1)];
    }

    private static (Dictionary<(string, string, string), LearningRecord> Tasks, Dictionary<string, PrOutcomeRecord> Outcomes, Dictionary<string, AutopsyRecord> Autopsies) Parse(string text)
    {
        Dictionary<(string, string, string), LearningRecord> tasks = [];
        Dictionary<string, PrOutcomeRecord> outcomes = [];
        Dictionary<string, AutopsyRecord> autopsies = [];
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var node = JsonNode.Parse(line)!.AsObject();
                switch (node["type"]?.GetValue<string>())
                {
                    case "task" when node.Deserialize(LearningJsonContext.Default.LearningRecord) is { } task:
                        tasks[KeyOf(task)] = task;
                        break;
                    case "pr_outcome" when node.Deserialize(LearningJsonContext.Default.PrOutcomeRecord) is { } outcome:
                        outcomes[outcome.RunId] = outcome;
                        break;
                    case "autopsy" when node.Deserialize(LearningJsonContext.Default.AutopsyRecord) is { } autopsy:
                        autopsies[autopsy.RunId] = autopsy;
                        break;
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException)
            {
            }
        }
        return (tasks, outcomes, autopsies);
    }

    private async Task WriteLine(string type, JsonNode node, CancellationToken ct)
    {
        node["type"] = type;
        var line = Encoding.UTF8.GetBytes(node.ToJsonString() + "\n");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        await file.WriteAsync(line, ct);
    }
}

/// <summary>Nearest-rank percentiles, the way <c>RoutingReport</c> takes them: the smallest value with at least that share of the values at or below it.</summary>
public static class LearningStats
{
    /// <returns>Null for no values.</returns>
    public static decimal? Percentile(IEnumerable<decimal> values, int percent)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(percent / 100m * sorted.Count) - 1, 0, sorted.Count - 1)];
    }
}
