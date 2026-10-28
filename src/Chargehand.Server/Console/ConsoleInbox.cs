using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.RunLog;

namespace Chargehand.Server;

/// <param name="Kind"><c>question</c>, <c>pull_request</c> or <c>failure</c>.</param>
/// <param name="Reason">For a pull request: <c>policy_ask</c>, <c>test_integrity</c> or <c>partial_draft</c>. For a failure: the error code, or <c>partial</c> / <c>lost</c>.</param>
/// <param name="Subject">For a failure: <c>task</c>, <c>run</c> or <c>batch</c>.</param>
/// <param name="Answer">For a question: how to answer it, the body of a <c>POST /v1/runs</c> being a task with <c>continue_from</c> and <c>answers</c> (ADR 0045).</param>
/// <param name="Count">How many identical failures this item stands for (ADR 0064): the same subject, code, repository and text; the item is the newest of them.</param>
/// <param name="What">What happened, in one sentence (<see cref="InboxCatalog"/>, ADR 0085).</param>
/// <param name="Why">The cause in plain words.</param>
/// <param name="Options">What a person can do about it, each with its effect: only options that can succeed.</param>
public sealed record InboxItem(string Kind, string Id, DateTimeOffset At, string? Actor, string? BatchRunId, string? TaskId, string? RunId, string? Repository, string Title,
    IReadOnlyList<string> Questions, AnswerHint? Answer, PullRequestBody? PullRequest, ErrorBody? Error, string? Reason, string? Subject, int Count = 1, string? What = null, string? Why = null,
    IReadOnlyList<InboxOption>? Options = null);

public sealed record InboxCounts(int Questions, int PullRequests, int Failures, int Total);

/// <summary>What left the inbox in the window without being dealt with there (ADR 0064), counted in the items as they were before <see cref="Duplicates"/> folded any.</summary>
/// <param name="Superseded">A later batch task or run of the same account replaced it.</param>
/// <param name="Infrastructure">A failure of chargehand's own (<see cref="InboxRules.InfrastructureCodes"/>), and a later run on the repository finished ok.</param>
/// <param name="Continued">A question that a batch answered or retried.</param>
/// <param name="Dismissed">A person dismissed it.</param>
/// <param name="Duplicates">Open failures folded into an identical newer one (its <c>count</c>).</param>
/// <param name="PrClosed">A failure about a pull request that was merged or closed since.</param>
public sealed record InboxResolved(int Superseded, int Infrastructure, int Continued, int Dismissed, int Duplicates, int PrClosed = 0);

/// <summary>Open failures with the same repository and code (CHARGEHAND-237), so the console shows them as one card. Only for two items or more; each item stays in <c>items</c>.</summary>
/// <param name="Id">The id to dismiss or restore the whole group by: <c>group:</c>, the code, and the newest item's id.</param>
/// <param name="Count">The failures it stands for: the sum of its items' <c>count</c>.</param>
/// <param name="At">When the newest failure in it happened.</param>
/// <param name="Items">Its item ids, newest first; with a <c>limit</c> some may not be in <c>items</c>.</param>
public sealed record InboxGroup(string Id, string Kind, string? Repository, string? Reason, int Count, DateTimeOffset At, IReadOnlyList<string> Items);

/// <summary><c>inbox/v1</c>: what needs a person, newest first.</summary>
public sealed record InboxBody(string ContractVersion, DateTimeOffset GeneratedAt, DateTimeOffset Since, InboxCounts Counts, IReadOnlyList<InboxItem> Items, IReadOnlyList<string> NotRecorded,
    InboxResolved Resolved, IReadOnlyList<InboxGroup>? Groups = null);

/// <summary>An open item and the ids of the items it stands for.</summary>
internal sealed record InboxEntry(InboxItem Item, IReadOnlyList<string> Members);

/// <summary>A group as the inbox shows it, and every item id its entries stand for (what dismissing it dismisses).</summary>
internal sealed record InboxGroupEntry(InboxGroup Group, IReadOnlyList<string> Members);

/// <summary>The inbox before the limit: the open entries, newest first, and why each other item left.</summary>
/// <param name="Closed">Item id to <see cref="InboxRules"/>' reason or <c>dismissed</c>.</param>
internal sealed record InboxView(IReadOnlyList<InboxEntry> Open, ILookup<string, string> Closed, InboxResolved Resolved, IReadOnlyList<InboxGroupEntry> Groups);

/// <summary>The "needs you" inbox of the console: task questions nobody answered, pull requests chargehand will not merge, and failures. Derived from the run log and the learning records on every call:
/// an item leaves when what it names changes (a pull request is merged or closed), when a rule of <see cref="InboxRules"/> resolves it, or when a person dismissed it (the one thing stored).</summary>
internal static class ConsoleInbox
{
    public const string Contract = "inbox/v1";

    public const string Dismissed = "dismissed";

    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(14);

    private static readonly IReadOnlyList<string> NotRecorded =
    [
        "A pull request's state is read back from the forge about every hour for 14 days (driven.learning), so one a person closed minutes ago can still read open.",
        "Pull requests that chargehand did not open, and a batch the integrator left on 'ask' before this server wrote learning records, are not listed.",
        "Superseded and infrastructure items are resolved by rules over the run log (ADR 0064); a rule can resolve an item a person still meant to look at, and resolved items are counted, not listed.",
        "A dismissal is kept by item id: a new failure of the same kind is a new item, and an owner's dismissal hides an account's item from the account too.",
        "Whether a pull request was merged or closed is known only for pull requests chargehand opened; a review of anyone else's stays open until a later review of the same head completes, or Dismiss.",
    ];

    public static async Task<InboxBody> BuildAsync(ConsoleContext c, Principal? who, DateTimeOffset since, int limit, CancellationToken ct)
    {
        var view = await ViewAsync(c, who, since, ct);
        var open = view.Open.Select(e => e.Item).ToList();
        var counts = new InboxCounts(open.Count(i => i.Kind == "question"), open.Count(i => i.Kind == "pull_request"), open.Count(i => i.Kind == "failure"), open.Count);
        return new InboxBody(Contract, c.Now, since, counts, [.. open.Take(Math.Clamp(limit, 1, 500))], NotRecorded, view.Resolved, [.. view.Groups.Select(g => g.Group)]);
    }

    /// <summary>Counts only, for the overview.</summary>
    public static async Task<InboxCounts> CountsAsync(ConsoleContext c, Principal? who, CancellationToken ct) =>
        (await BuildAsync(c, who, c.Now - DefaultWindow, 1, ct)).Counts;

    public static async Task<InboxView> ViewAsync(ConsoleContext c, Principal? who, DateTimeOffset since, CancellationToken ct)
    {
        var index = await c.IndexAsync(ct);
        var everything = await c.LearningAsync(ct);
        var learning = everything.Where(e => Principal.Sees(who, e.Record.Actor)).ToList();
        var candidates = Questions(index, who, since).Concat(PullRequests(c, learning, since)).Concat(Failures(c, index, learning, who, since)).ToList();
        var rules = new InboxRules(index, everything);
        var dismissed = c.Dismissals?.Read() ?? new Dictionary<string, InboxDismissal>();
        var byRun = everything.GroupBy(e => e.Record.RunId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Record, StringComparer.Ordinal);
        var decided = candidates.Select(x => (Candidate: x, Reason: rules.Resolve(x) ?? (dismissed.ContainsKey(x.Item.Id) ? Dismissed : null))).ToList();
        // Identical open failures fold into the newest; every other item is its own entry.
        var open = decided.Where(d => d.Reason is null).Select(d => d.Candidate)
            .GroupBy(x => x.Item.Kind == "failure" ? (x.Item.Kind, x.Item.Subject, x.Code, x.Item.Repository, x.Text) : (x.Item.Kind, x.Item.Id, null, null, ""))
            .Select(g => g.OrderByDescending(x => x.Item.At).ToList())
            .Select(g => new InboxEntry(InboxCatalog.Describe(g[0], g[0].Item.RunId is { } run ? byRun.GetValueOrDefault(run) : null) with { Count = g.Count }, [.. g.Select(x => x.Item.Id)]))
            .OrderByDescending(e => e.Item.At).ToList();
        int Of(string reason) => decided.Count(d => d.Reason == reason);
        var resolved = new InboxResolved(Of(InboxRules.Superseded), Of(InboxRules.Infrastructure), Of(InboxRules.Continued), Of(Dismissed), open.Sum(e => e.Members.Count - 1), Of(InboxRules.PrClosed));
        return new InboxView(open, decided.Where(d => d.Reason is not null).ToLookup(d => d.Candidate.Item.Id, d => d.Reason!), resolved, Groups(open));
    }

    public const string GroupPrefix = "group:";

    /// <summary>Open failures with the same repository and code, two or more, newest group first.</summary>
    private static List<InboxGroupEntry> Groups(IReadOnlyList<InboxEntry> open) =>
        [.. open.Where(e => e.Item.Kind == "failure").GroupBy(e => (e.Item.Repository, e.Item.Reason)).Where(g => g.Count() > 1)
            .Select(g => g.ToList())
            .Select(g => new InboxGroupEntry(new InboxGroup($"{GroupPrefix}{g[0].Item.Reason}:{g[0].Item.Id}", "failure", g[0].Item.Repository, g[0].Item.Reason, g.Sum(e => e.Item.Count), g[0].Item.At,
                [.. g.Select(e => e.Item.Id)]), [.. g.SelectMany(e => e.Members).Distinct(StringComparer.Ordinal)]))];

    private static IEnumerable<InboxCandidate> Questions(RunIndex index, Principal? who, DateTimeOffset since)
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
                if (ResultArtifacts.String(row, "run_id") is not { } runId)
                    continue;
                var run = index.RunOf(runId);
                var goal = index.Starts.GetValueOrDefault(runId)?.Request.Text ?? taskId;
                var item = new InboxItem("question", $"question:{start.RunId}:{taskId}", run?.Finished ?? batch.Finished, start.Actor ?? batch.Actor, start.RunId, taskId, runId, repository?.Path,
                    Title(goal), run?.Result.OpenQuestions ?? [], new AnswerHint("POST", "/v1/runs", new ContinueFrom(start.RunId, taskId), repository, "driven"), null, null, null, null);
                yield return new InboxCandidate(item, repository?.Path, goal, start.Started, null, [], InboxRules.AskedOf(start, taskId));
            }
        }
    }

    private static IEnumerable<InboxCandidate> PullRequests(ConsoleContext c, IReadOnlyList<LearningEntry> learning, DateTimeOffset since)
    {
        foreach (var entry in learning.Where(e => e.Record.At >= since))
            if (ConsoleBatches.PullRequestOf(c, entry) is { AwaitingPerson: true } pr)
                yield return new InboxCandidate(new InboxItem("pull_request", $"pull_request:{pr.Url}", entry.Record.At, entry.Record.Actor, entry.Record.BatchId, entry.Record.TaskId, entry.Record.RunId,
                    pr.Repository, $"{pr.Repository} #{pr.Number}", [], null, pr, null, pr.AwaitingReason, null), pr.Repository, "", entry.Record.At, null, []);
    }

    private static IEnumerable<InboxCandidate> Failures(ConsoleContext c, RunIndex index, IReadOnlyList<LearningEntry> learning, Principal? who, DateTimeOffset since)
    {
        foreach (var entry in learning.Where(e => e.Record.At >= since && (e.Record.Outcome == LearningOutcome.Failed || (e.Record.Outcome == LearningOutcome.Partial && e.Record.Pr is null))))
        {
            var r = entry.Record;
            var result = index.RunOf(r.RunId)?.Result;
            var error = result?.Error is { } e ? ConsoleBatches.ErrorOf(e) : null;
            if (error?.Code == "cancelled")
                continue;
            var text = index.Starts.GetValueOrDefault(r.RunId)?.Request.Text ?? r.TaskId;
            var code = error?.Code ?? (r.Outcome == LearningOutcome.Partial ? "partial" : r.StopReason);
            var batch = index.Starts.GetValueOrDefault(r.BatchId);
            yield return new InboxCandidate(new InboxItem("failure", $"failure:{r.BatchId}:{r.TaskId}", r.At, r.Actor, r.BatchId, r.TaskId, r.RunId, r.Repository, Title(text), [], null, null, error, code, "task"),
                batch?.Request.Context.Repository?.Path ?? r.Repository, text, batch?.Started ?? r.At, code, [], InboxRules.AskedOf(batch, r.TaskId));
        }
        // A run of its own (not a task of a batch, not a call of a session) that failed, was denied, or lost its process.
        foreach (var start in index.Starts.Values.Where(s => s.ParentRunId is null && s.Started >= since && Principal.Sees(who, s.Actor)))
            if (RunFailure(c, index, start) is { } item)
                yield return new InboxCandidate(item, start.Request.Context.Repository?.Path, start.Request.Text, start.Started, item.Reason, start.Request.Driven?.Tasks.Select(t => t.Id).ToList() ?? []);
    }

    private static InboxItem? RunFailure(ConsoleContext c, RunIndex index, StartRecord start)
    {
        var run = index.RunOf(start.RunId);
        var batch = start.Request.Driven is not null;
        var path = start.Request.Context.Repository?.Path;
        if (run is null)
            return !start.OwnerAlive() && c.Runs.Find(start.RunId) is null
                ? new InboxItem("failure", $"failure:{start.RunId}", start.Started, start.Actor, batch ? start.RunId : null, null, start.RunId, path, Title(start.Request.Text),
                    [], null, null, new ErrorBody("lost", "the process that ran it ended before it finished", false, null), "lost", batch ? "batch" : "run")
                : null;
        if (run.Result.Status is not (ResultStatus.Failed or ResultStatus.Denied) || run.Result.Error?.Code is ErrorCode.Cancelled)
            return null;
        // A batch that failed only because tasks did not finish is already listed task by task.
        if (batch && run.Result.Error?.Code is ErrorCode.TasksIncomplete)
            return null;
        return new InboxItem("failure", $"failure:{start.RunId}", run.Finished, start.Actor, batch ? start.RunId : null, null, start.RunId, path, Title(start.Request.Text),
            [], null, null, run.Result.Error is { } e ? ConsoleBatches.ErrorOf(e) : null, run.Result.Error is { } x ? JsonNamingPolicy.SnakeCaseLower.ConvertName(x.Code.ToString()) : "failed", batch ? "batch" : "run");
    }

    private static string Title(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return ResultArtifacts.Cut(line, 120);
    }
}
