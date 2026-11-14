using System.Globalization;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Modules.Oversight.Api;

/// <summary>The root-cause classes a failure autopsy names (issue #454). <see cref="Other"/> is the model's answer when none fits; <see cref="All"/> is what the prompt offers it.</summary>
public static class FailureClass
{
    public const string Auth = "auth";
    public const string OomKill = "oom_kill";
    public const string Verification = "verification";
    public const string TurnLimitNoCommits = "turn_limit_no_commits";
    public const string SecretGuard = "secret_guard";
    public const string RepoPath = "repo_path";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [Auth, OomKill, Verification, TurnLimitNoCommits, SecretGuard, RepoPath, Other];
}

/// <summary>A failure autopsy's answer (issue #454), the learning log's third line kind next to <c>task</c> and <c>pr_outcome</c>: a short root-cause class for a task that ended
/// <see cref="LearningOutcome.Failed"/> or <see cref="LearningOutcome.Partial"/>, from one cheap model call over the task's own learning record. The latest line for a run id wins, as
/// for <see cref="PrOutcomeRecord"/>.</summary>
/// <param name="Class">One of <see cref="FailureClass.All"/>; a value the model answered that is not on the list is kept as <see cref="FailureClass.Other"/> by the caller, never written raw.</param>
/// <param name="Reason">At most <see cref="ModelFailureAutopsy.MaxReasonLength"/> characters of plain text; null when the model gave none.</param>
public sealed record AutopsyRecord(string RunId, DateTimeOffset At, string Class, string? Reason = null);

/// <param name="Request">The task's batch request: the repository the autopsy run is pinned to, as the retro run is.</param>
/// <param name="BatchId">The batch's run id; the autopsy run is recorded as its child.</param>
/// <param name="Record">The task's own learning record: counts, sizes, the outcome and stop reason. No goal or session text.</param>
public sealed record AutopsyInput(RunRequest Request, string BatchId, LearningRecord Record);

public sealed class AutopsyException(string message) : Exception(message);

/// <summary>Classifies why a driven task ended <see cref="LearningOutcome.Failed"/> or <see cref="LearningOutcome.Partial"/> (issue #454).</summary>
public interface IFailureAutopsy
{
    /// <exception cref="AutopsyException">The autopsy run ran and its answer cannot be used.</exception>
    Task<AutopsyRecord> ClassifyAsync(AutopsyInput input, CancellationToken ct);
}

/// <summary>Where a written autopsy goes besides the learning log: the extension point for filing the top clusters to a tracker's inbox (issue #454). chargehand has no write path to a
/// tracker yet (the task source only reads), so no sink ships; a deployment that adds one is called by the weekly job, and a sink that throws is reported and ignored.</summary>
public interface IFailureClusterSink
{
    Task PublishAsync(IReadOnlyList<FailureCluster> clusters, CancellationToken ct);
}

/// <summary>One root-cause class's share of a window's failed or partial tasks, largest first: how many autopsies named it and which runs they were.</summary>
public sealed record FailureCluster(string Class, int Count, IReadOnlyList<string> RunIds);

/// <summary>The same call the orchestrator makes for a plan or a retro: a run on a read-only preset, through its own pipeline.</summary>
public delegate Task<ResultContract> AutopsyRunner(RunRequest request, string runId, string parentRunId, CancellationToken ct);

/// <summary>The autopsy of a failed or partial task (issue #454): one run on a read-only preset (<c>cheap</c> by default), like the planning step and the batch retro. It is given the task's
/// own learning record (size, spend, outcome, stop reason, governor counts, error detail; never a goal or a session's text) and a fixed list of root-cause classes, and answers with one of them
/// plus a short reason. Off unless <c>driven.learning.autopsy</c>.</summary>
public sealed class ModelAutopsy(AutopsyRunner run, string preset) : IFailureAutopsy
{
    public const int MaxReasonLength = 200;

    public async Task<AutopsyRecord> ClassifyAsync(AutopsyInput input, CancellationToken ct)
    {
        var runId = RunIds.New();
        var request = new RunRequest("request/v1", Prompt(input.Record), new RequestContext(false, preset, Repository: input.Request.Context.Repository));
        var result = await run(request, runId, input.BatchId, ct);
        if (result.Status != ResultStatus.Completed)
            throw new AutopsyException($"the autopsy run ended {JsonNamingPolicy.SnakeCaseLower.ConvertName(result.Status.ToString())}: {result.Error?.Message ?? result.Summary}");
        var answer = result.Summary.Trim();
        if (answer.Length == 0)
            throw new AutopsyException("the autopsy run printed nothing");
        var (cls, reason) = Split(answer);
        return new AutopsyRecord(input.Record.RunId, DateTimeOffset.UtcNow, FailureClass.All.Contains(cls) ? cls : FailureClass.Other,
            reason is { Length: > 0 } ? reason.Length > MaxReasonLength ? reason[..MaxReasonLength].TrimEnd() + " [cut]" : reason : null);
    }

    private static (string Class, string? Reason) Split(string answer)
    {
        var space = answer.IndexOf(' ');
        return space < 0 ? (answer.ToLowerInvariant(), null) : (answer[..space].ToLowerInvariant(), answer[(space + 1)..].Trim());
    }

    public static string Prompt(LearningRecord record)
    {
        var text = new StringBuilder();
        text.Append("Answer this question from the data below. It is one question with one answer: do not split it, do not ask for anything, do not read the repository and do not change any file.\n\n");
        text.Append("A driven coding task ended without completing. Name the single best root-cause class for why, from this fixed list (answer with exactly one of these words, nothing else on that line): ");
        text.Append(string.Join(", ", FailureClass.All));
        text.Append(". Use `other` when none fits or the data is too thin to say. After the class, on the same line, add one short reason of at most 20 words. Put the whole answer (class then reason) in `summary` as plain text, one line.\n\n");
        text.Append(CultureInfo.InvariantCulture, $"outcome: {record.Outcome}\nstop_reason: {record.StopReason ?? "(none)"}\ngovernor_mode: {record.GovernorMode ?? "(none)"}\n");
        text.Append(CultureInfo.InvariantCulture, $"turns: {record.Spend.Turns}\ncontinuations: {record.Spend.Continuations}\ntest_integrity_flagged: {record.TestIntegrityFlagged}\n");
        text.Append(CultureInfo.InvariantCulture, $"size: {record.SizeClass}\nbudget_usd: {record.Budget.Usd?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}\nspend_usd: {record.Spend.UsdEstimate?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}\n");
        if (record.Governor.Count > 0)
            text.Append(CultureInfo.InvariantCulture, $"governor: {string.Join(", ", record.Governor.Select(g => $"{g.Signal}/{g.Action} x{g.Count} (acted {g.Acted})"))}\n");
        return text.ToString();
    }
}

/// <summary>The top root-cause clusters of a window's failed and partial tasks (issue #454), from the learning log's autopsy records: the weekly job's input before it files tracker items.</summary>
public static class FailureClusterReport
{
    /// <summary>Entries with no autopsy, or outside <paramref name="since"/>, are left out. Ties keep the learning log's order (oldest first).</summary>
    public static IReadOnlyList<FailureCluster> Top(IEnumerable<LearningEntry> entries, DateTimeOffset since, int top) =>
        [.. entries.Where(e => e.Autopsy is not null && e.Record.At >= since)
            .GroupBy(e => e.Autopsy!.Class)
            .Select(g => new FailureCluster(g.Key, g.Count(), [.. g.Select(e => e.Record.RunId)]))
            .OrderByDescending(c => c.Count)
            .Take(top)];
}
