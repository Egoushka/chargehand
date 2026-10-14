using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Chargehand.Kernel;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Modules.Tasks.Api;

/// <summary>What the task leaves behind: its sessions and governor log for the console (CHARGEHAND-214), what it taught (ADR 0045), the budget it was held to, and its span.</summary>
public sealed partial class TaskRunner
{
    /// <summary>Keeps one session for the console (CHARGEHAND-214), with the runner it ran on.</summary>
    private void RecordSession(ResolvedTask task, string role, string runId, DateTimeOffset started, DateTimeOffset ended, SessionOutcome session)
    {
        if (state is not { KeepsRecords: true })
            return;
        state.Append(new SessionRecord(task.Id, runId, role, RunnerOf(task), started, ended, JsonNamingPolicy.SnakeCaseLower.ConvertName(session.Status.ToString()),
            session.Reason.Length > 0 ? session.Reason : null, session.Turns, session.InputTokens, session.OutputTokens, session.CacheReadTokens, session.CacheWriteTokens, session.UsdEstimate,
            settings.Priced ? session.CostUsd : null, session.ExitCode, session.Commits));
    }

    /// <summary>A session that ended before its outcome could be read: a cancel, or a failure to start or to read it back.</summary>
    private void RecordCutShort(ResolvedTask task, (string RunId, DateTimeOffset Started, string Role) session, Exception e)
    {
        if (state is not { KeepsRecords: true })
            return;
        var cancelled = e is OperationCanceledException;
        state.Append(new SessionRecord(task.Id, session.RunId, session.Role, RunnerOf(task), session.Started, DateTimeOffset.UtcNow, cancelled ? "cancelled" : "error",
            cancelled ? null : ChargehandException.ErrorOf(e).Message, 0, 0, 0, 0, 0, null, null, null, 0));
    }

    private static string? RunnerOf(ResolvedTask task) => string.IsNullOrEmpty(task.Runner) ? null : task.Runner;

    /// <summary>The session's <c>governor.jsonl</c> in full; the outcome file only has the first <see cref="SessionDriver.MaxGovernorDecisions"/>, which stand in for a runner too old to serve the log.</summary>
    private void RecordGovernor(ResolvedTask task, string runId, string outDirectory, SessionOutcome session)
    {
        if (state is not { KeepsRecords: true })
            return;
        var all = SessionDriver.ReadGovernorLog(outDirectory) ?? session.Governor ?? [];
        if (all.Count > 0)
            state.Append(new GovernorRecord(task.Id, runId, all.Count, [.. all.Take(GovernorRecord.MaxDecisions)]));
    }

    private static string StopReason(SessionOutcome session) =>
        session.Reason.Length > 0 ? session.Reason : JsonNamingPolicy.SnakeCaseLower.ConvertName(session.Status.ToString());

    /// <summary>A governor decision as a run event's detail: what it would do, the signal and the rule, the turn, and the spend against the budget when known.</summary>
    public static string Describe(GovernorDecision d)
    {
        var spend = d.SpendUsd is { } usd && d.BudgetUsd is { } budget ? string.Create(CultureInfo.InvariantCulture, $", spend {usd:0.00} of {budget:0.00}{Source(d.BudgetSource)}") : "";
        var mode = d.Acted ? "" : $"{JsonNamingPolicy.SnakeCaseLower.ConvertName(d.Mode.ToString())}: ";
        return string.Create(CultureInfo.InvariantCulture, $"governor {mode}{JsonNamingPolicy.SnakeCaseLower.ConvertName(d.Action.ToString())} on {d.Signal} ({d.Rule.Replace('_', ' ')}) at turn {d.Turn}{spend}");
    }

    private static string Source(BudgetSource? source) => source is { Kind: "history" } h
        ? string.Create(CultureInfo.InvariantCulture, $" (p{h.Percentile} of {h.N} past tasks{(h.Scope == "all" ? ", all repositories" : "")})") : "";

    /// <summary>What the task taught (ADR 0045), written once it has ended. A store that cannot be written must not turn a finished task into a failure.</summary>
    /// <param name="last">The last session: how the task stopped.</param>
    /// <param name="spent">The whole task's usage, the CI fix sessions' included.</param>
    private async Task LearnAsync(ResolvedTask task, TaskOutcome outcome, SessionOutcome last, SessionOutcome spent, int continuations, IReadOnlyList<GovernorDecision> decisions, HandoverOutcome? handed,
        bool partial, string? head, BudgetChoice? budget)
    {
        if (learning is null)
            return;
        var pr = handed?.PullRequest;
        var record = new LearningRecord(settings.BatchId, task.Id, outcome.RunId!, DateTimeOffset.UtcNow, LearningRepository(), task.Size ?? "M", task.Size is null ? "default" : task.SizeFrom ?? "caller",
            new LearningBudget(budget?.Usd, budget?.Source ?? new BudgetSource("none")),
            new LearningSpend(spent.UsdEstimate, spent.UsdEstimatePartial, spent.InputTokens, spent.OutputTokens, spent.CacheReadTokens, spent.CacheWriteTokens, spent.Turns, continuations),
            partial ? LearningOutcome.Partial : outcome.State switch { TaskState.Completed => LearningOutcome.Completed, TaskState.NeedsInput => LearningOutcome.NeedsInput, _ => LearningOutcome.Failed },
            StopReason(last), settings.Governor is { } governor ? JsonNamingPolicy.SnakeCaseLower.ConvertName(governor.Mode.ToString()) : null, Summarize(decisions), handed?.TestIntegrity is not null,
            pr is not null && settings.Remote is { } remote ? new LearningPr(pr.Url, pr.Number, remote, head) : null, settings.Actor,
            _memoryPeaks.TryGetValue(task.Id, out var peakMb) ? peakMb : null);
        try
        {
            await learning.AppendAsync(record, CancellationToken.None);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"chargehand: could not write the learning record of task {task.Id}: {e.Message}");
        }
    }

    /// <summary>The task's budget (ADR 0045): its own, else a percentile of what past tasks of its size class cost when there are enough, else the flat one. Chosen once when the task starts, so its
    /// sessions all hold it to the same figure. Null without a governor.</summary>
    private async Task<BudgetChoice?> BudgetAsync(ResolvedTask task)
    {
        if (settings.Governor is not { } governor)
            return null;
        IReadOnlyList<LearningRecord> records = [];
        if (learning is not null && task.BudgetUsd is null)
        {
            try
            {
                records = [.. (await learning.ReadAsync(CancellationToken.None)).Select(e => e.Record)];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"chargehand: could not read the learning records for the budget of task {task.Id}: {e.Message}");
            }
        }
        return BudgetHistory.Choose(governor, task.Size, task.BudgetUsd, LearningRepository(), records, DateTimeOffset.UtcNow);
    }

    private string LearningRepository() => RepositoryKey(settings.Remote, settings.Request.Context.Repository?.Path);

    /// <summary>How learning records name a repository, so budgets and admission requests find the records of the one they are for.</summary>
    public static string RepositoryKey(RemoteRepository? remote, string? path) =>
        remote is not null ? remote.Host is null ? $"{remote.Owner}/{remote.Name}" : $"{remote.Host}/{remote.Owner}/{remote.Name}" : path ?? "unknown";

    internal static IReadOnlyList<GovernorCount> Summarize(IEnumerable<GovernorDecision> decisions) =>
        [.. decisions.GroupBy(d => (d.Signal, Action: JsonNamingPolicy.SnakeCaseLower.ConvertName(d.Action.ToString())))
            .OrderBy(g => g.Key.Signal, StringComparer.Ordinal).ThenBy(g => g.Key.Action, StringComparer.Ordinal)
            .Select(g => new GovernorCount(g.Key.Signal, g.Key.Action, g.Count(), g.Count(d => d.Acted)))];

    private static void Tag(Activity? span, TaskOutcome outcome, SessionOutcome session, DateTimeOffset started)
    {
        if (span is null)
            return;
        span.SetTag("chargehand.task.state", JsonNamingPolicy.SnakeCaseLower.ConvertName(outcome.State.ToString()));
        span.SetTag("chargehand.error.code", outcome.Error is { } code ? JsonNamingPolicy.SnakeCaseLower.ConvertName(code.ToString()) : null);
        span.SetTag("chargehand.task.detail", outcome.Detail);
        span.SetTag("chargehand.branch", outcome.Branch);
        span.SetTag("chargehand.pr_url", outcome.PrUrl);
        span.SetTag("gen_ai.request.model", session.Model);
        span.SetTag("chargehand.claude_code.session_id", session.SessionId);
        span.SetTag("chargehand.session.turns", session.Turns);
        span.SetTag("chargehand.task.tokens", outcome.Tokens);
        span.SetTag("chargehand.task.wall_seconds", Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1));
        if (outcome.State != TaskState.Completed)
            span.SetStatus(ActivityStatusCode.Error, outcome.Detail);
    }
}
