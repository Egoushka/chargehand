using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;

namespace Chargehand.Server;

/// <param name="Kind"><c>question</c>, <c>pull_request</c> or <c>failure</c>.</param>
/// <param name="Reason">For a pull request: <c>policy_ask</c>, <c>test_integrity</c> or <c>partial_draft</c>. For a failure: the error code, or <c>partial</c> / <c>lost</c>.</param>
/// <param name="Subject">For a failure: <c>task</c>, <c>run</c> or <c>batch</c>.</param>
/// <param name="Answer">For a question: how to answer it, the body of a <c>POST /v1/runs</c> being a task with <c>continue_from</c> and <c>answers</c> (ADR 0045).</param>
public sealed record InboxItem(string Kind, string Id, DateTimeOffset At, string? Actor, string? BatchRunId, string? TaskId, string? RunId, string? Repository, string Title,
    IReadOnlyList<string> Questions, AnswerHint? Answer, PullRequestBody? PullRequest, ErrorBody? Error, string? Reason, string? Subject);

public sealed record InboxCounts(int Questions, int PullRequests, int Failures, int Total);

/// <summary><c>inbox/v1</c>: what needs a person, newest first.</summary>
public sealed record InboxBody(string ContractVersion, DateTimeOffset GeneratedAt, DateTimeOffset Since, InboxCounts Counts, IReadOnlyList<InboxItem> Items, IReadOnlyList<string> NotRecorded);

/// <summary>The "needs you" inbox of the console: task questions nobody answered, pull requests chargehand will not merge, and failures. Derived from the run log and the learning records on every call;
/// nothing is stored, so an item leaves the inbox when what it names changes (a question is answered by a batch that continues it, a pull request is merged or closed).</summary>
internal static class ConsoleInbox
{
    public const string Contract = "inbox/v1";

    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(14);

    private static readonly IReadOnlyList<string> NotRecorded =
    [
        "A pull request's state is read back from the forge about every hour for 14 days (driven.learning), so one a person closed minutes ago can still read open.",
        "Pull requests that chargehand did not open, and a batch the integrator left on 'ask' before this server wrote learning records, are not listed.",
        "A failure is listed once per task or run; whether someone already looked at it is not recorded.",
    ];

    public static async Task<InboxBody> BuildAsync(ConsoleContext c, Principal? who, DateTimeOffset since, int limit, CancellationToken ct)
    {
        var index = await c.IndexAsync(ct);
        var learning = (await c.LearningAsync(ct)).Where(e => Principal.Sees(who, e.Record.Actor)).ToList();
        var all = Questions(c, index, who, since).Concat(PullRequests(c, learning, since)).Concat(Failures(c, index, learning, who, since)).OrderByDescending(i => i.At).ToList();
        var counts = new InboxCounts(all.Count(i => i.Kind == "question"), all.Count(i => i.Kind == "pull_request"), all.Count(i => i.Kind == "failure"), all.Count);
        return new InboxBody(Contract, c.Now, since, counts, [.. all.Take(Math.Clamp(limit, 1, 500))], NotRecorded);
    }

    /// <summary>Counts only, for the overview.</summary>
    public static async Task<InboxCounts> CountsAsync(ConsoleContext c, Principal? who, CancellationToken ct) =>
        (await BuildAsync(c, who, c.Now - DefaultWindow, 1, ct)).Counts;

    private static IEnumerable<InboxItem> Questions(ConsoleContext c, RunIndex index, Principal? who, DateTimeOffset since)
    {
        foreach (var start in index.Batches.Where(s => Principal.Sees(who, s.Actor) && s.Started >= since).OrderByDescending(s => s.Started))
        {
            if (index.RunOf(start.RunId) is not { } batch || ResultArtifacts.Content(batch.Result, "driven-batch") is not { ValueKind: JsonValueKind.Object } artifact
                || !artifact.TryGetProperty("tasks", out var rows) || rows.ValueKind != JsonValueKind.Array)
                continue;
            var repository = start.Request.Context.Repository is { } r ? new BatchRepository(r.Path, r.Commit) : null;
            foreach (var row in rows.EnumerateArray().Where(x => ResultArtifacts.String(x, "status") == "needs_input" && ResultArtifacts.String(x, "id") is not null))
            {
                var taskId = ResultArtifacts.String(row, "id")!;
                var runId = ResultArtifacts.String(row, "run_id");
                if (runId is null || index.ContinuationsOf(start.RunId, taskId).Any())
                    continue;
                var run = index.RunOf(runId);
                var goal = index.Starts.GetValueOrDefault(runId)?.Request.Text ?? taskId;
                yield return new InboxItem("question", $"question:{start.RunId}:{taskId}", run?.Finished ?? batch.Finished, start.Actor ?? batch.Actor, start.RunId, taskId, runId, repository?.Path,
                    Title(goal), run?.Result.OpenQuestions ?? [], new AnswerHint("POST", "/v1/runs", new ContinueFrom(start.RunId, taskId), repository, "driven"), null, null, null, null);
            }
        }
    }

    private static IEnumerable<InboxItem> PullRequests(ConsoleContext c, IReadOnlyList<LearningEntry> learning, DateTimeOffset since)
    {
        foreach (var entry in learning.Where(e => e.Record.At >= since))
            if (ConsoleBatches.PullRequestOf(c, entry) is { AwaitingPerson: true } pr)
                yield return new InboxItem("pull_request", $"pull_request:{pr.Url}", entry.Record.At, entry.Record.Actor, entry.Record.BatchId, entry.Record.TaskId, entry.Record.RunId, pr.Repository,
                    $"{pr.Repository} #{pr.Number}", [], null, pr, null, pr.AwaitingReason, null);
    }

    private static IEnumerable<InboxItem> Failures(ConsoleContext c, RunIndex index, IReadOnlyList<LearningEntry> learning, Principal? who, DateTimeOffset since)
    {
        foreach (var entry in learning.Where(e => e.Record.At >= since && (e.Record.Outcome == LearningOutcome.Failed || (e.Record.Outcome == LearningOutcome.Partial && e.Record.Pr is null))))
        {
            var r = entry.Record;
            var result = index.RunOf(r.RunId)?.Result;
            var error = result?.Error is { } e ? ConsoleBatches.ErrorOf(e) : null;
            if (error?.Code == "cancelled")
                continue;
            yield return new InboxItem("failure", $"failure:{r.BatchId}:{r.TaskId}", r.At, r.Actor, r.BatchId, r.TaskId, r.RunId, r.Repository, Title(index.Starts.GetValueOrDefault(r.RunId)?.Request.Text ?? r.TaskId),
                [], null, null, error, error?.Code ?? (r.Outcome == LearningOutcome.Partial ? "partial" : r.StopReason), "task");
        }
        // A run of its own (not a task of a batch, not a call of a session) that failed, was denied, or lost its process.
        foreach (var start in index.Starts.Values.Where(s => s.ParentRunId is null && s.Started >= since && Principal.Sees(who, s.Actor)))
        {
            var run = index.RunOf(start.RunId);
            var batch = start.Request.Driven is not null;
            if (run is null)
            {
                if (!start.OwnerAlive() && c.Runs.Find(start.RunId) is null)
                    yield return new InboxItem("failure", $"failure:{start.RunId}", start.Started, start.Actor, batch ? start.RunId : null, null, start.RunId, start.Request.Context.Repository?.Path, Title(start.Request.Text),
                        [], null, null, new ErrorBody("lost", "the process that ran it ended before it finished", false, null), "lost", batch ? "batch" : "run");
                continue;
            }
            if (run.Result.Status is not (ResultStatus.Failed or ResultStatus.Denied) || run.Result.Error?.Code is ErrorCode.Cancelled)
                continue;
            // A batch that failed only because tasks did not finish is already listed task by task.
            if (batch && run.Result.Error?.Code is ErrorCode.TasksIncomplete)
                continue;
            yield return new InboxItem("failure", $"failure:{start.RunId}", run.Finished, start.Actor, batch ? start.RunId : null, null, start.RunId, start.Request.Context.Repository?.Path, Title(start.Request.Text),
                [], null, null, run.Result.Error is { } e ? ConsoleBatches.ErrorOf(e) : null, run.Result.Error is { } x ? JsonNamingPolicy.SnakeCaseLower.ConvertName(x.Code.ToString()) : "failed", batch ? "batch" : "run");
        }
    }

    private static string Title(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return ResultArtifacts.Cut(line, 120);
    }
}
