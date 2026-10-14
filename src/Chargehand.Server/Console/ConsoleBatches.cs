using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.RunLog;

namespace Chargehand.Server;

public sealed record BatchRepository(string Path, string Commit);

/// <summary>The batch's own limits as the request asked for them. A token or dollar cap left out is no cap.</summary>
public sealed record BatchLimitsBody(int? MaxParallel, long? MaxTokensTotal, decimal? MaxUsdTotal, bool? Plan);

public sealed record ErrorBody(string Code, string Message, bool Retryable, string? Action);

/// <summary>A task's budget in the governor's dollar equivalent, and where it came from: <c>task</c>, <c>flat</c>, <c>history</c> or <c>none</c> (ADR 0045).</summary>
public sealed record BudgetBody(decimal? Usd, BudgetSource Source);

/// <param name="UsdEstimate">The dollar equivalent of the tokens, also on a subscription; <paramref name="Usd"/> is the priced cost and is null where the credential has no price.</param>
public sealed record TaskSpendBody(decimal? Usd, decimal? UsdEstimate, bool UsdEstimatePartial, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, int? Turns, int Continuations);

/// <summary>One governor decision with its detail, from the session's <c>governor.jsonl</c> (ADR 0045). <paramref name="Session"/> is the run id of the session that made it.</summary>
public sealed record GovernorDecisionBody(DateTimeOffset At, string Session, int Turn, string Mode, string Action, string Signal, string Rule, IReadOnlyDictionary<string, string> Values, decimal? SpendUsd,
    decimal? BudgetUsd, string? SizeClass, bool Acted, BudgetSource? BudgetSource);

/// <param name="Decisions">Counts per signal and rung, from the learning record once the task has ended.</param>
/// <param name="Log">Every decision of the task's sessions, oldest first (the first 200 of each session); absent for a task that ran before they were kept.</param>
/// <param name="LogTotal">How many decisions the sessions' logs held, which is more than <paramref name="Log"/> when a session's was cut.</param>
public sealed record GovernorBody(string? Mode, string? StopReason, IReadOnlyList<GovernorCount> Decisions, IReadOnlyList<GovernorDecisionBody>? Log = null, int? LogTotal = null);

public sealed record CheckBody(string Name, bool Required, string Outcome, long? JobId);

/// <summary>The checks of a task's pull request at one look of the integrator's watch (ADR 0046). <paramref name="Decision"/> is what the CI fixer's policy made of it: <c>wait</c>, <c>done</c>, <c>fix</c> or <c>give_up</c>.</summary>
public sealed record CheckSnapshotBody(DateTimeOffset At, int Pull, string HeadSha, string MergeStatus, bool Draft, bool AutoMerge, string Decision, IReadOnlyList<CheckBody> Checks);

public sealed record CiAttemptBody(int Attempt, int MaxAttempts, DateTimeOffset At, string JobsKey, IReadOnlyList<string> Failing, bool Pushed, string Detail);

/// <summary>What the integrator saw of a task's pull request: the latest look at its checks, how many looks differed, every CI fix attempt, and in the task view the looks themselves.</summary>
public sealed record CiBody(CheckSnapshotBody? Checks, int Looks, IReadOnlyList<CiAttemptBody> Attempts, IReadOnlyList<CheckSnapshotBody>? History = null);

/// <summary>Where a running task's session stands, from its latest <c>session_progress</c> event; null for a task that is not running or has not reported yet.</summary>
public sealed record TaskLiveBody(DateTimeOffset At, SessionStage? Stage, int? Turns, long? Tokens, decimal? UsdEstimate, bool UsdEstimatePartial, long? CacheRead, long? CacheWrite);

/// <summary>A session of a task. <c>Role</c> is <c>first</c>, <c>continuation</c> or <c>ci_fix</c>; the rest is from the session's record and absent for a session that is still running or ran before they were kept.</summary>
public sealed record TaskSessionBody(string RunId, string Role, string? Runner = null, DateTimeOffset? Started = null, DateTimeOffset? Ended = null, string? Status = null, string? Reason = null, int? Turns = null,
    long? InputTokens = null, long? OutputTokens = null, long? CacheReadTokens = null, long? CacheWriteTokens = null, decimal? UsdEstimate = null, decimal? Usd = null, int? ExitCode = null, int? Commits = null);

public sealed record AnswerHint(string Method, string Path, ContinueFrom ContinueFrom, BatchRepository? Repository, string Preset);

public sealed record TaskLinkBody(string RunId, string TaskId);

/// <param name="Integrator">What the integrator does with this pull request (ADR 0046): the profile's mode for its repository, and the last thing it said (<c>integrated</c> event) when the run's events are still held.</param>
public sealed record PullRequestBody(string Url, int? Number, string? Repository, string BatchRunId, string TaskId, string? RunId, string State, DateTimeOffset? StateAt, bool Partial, bool TestIntegrityFlagged,
    string IntegratorMode, bool AwaitingPerson, string? AwaitingReason, string? IntegratorLast, string? Actor, DateTimeOffset? OpenedAt);

public sealed record BatchTaskBody(string Id, string? Ref, string? Goal, bool GoalTruncated, string? Size, string? SizeFrom, IReadOnlyList<string> DependsOn, string? SplitFrom, TaskLinkBody? Continues,
    string State, string? Phase, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? RunId, string? Container, string? Branch, string? PrUrl, ErrorBody? Error, string? Detail,
    BudgetBody? Budget, TaskSpendBody? Spend, GovernorBody? Governor, string? Outcome, bool TestIntegrityFlagged, TaskLiveBody? Live, IReadOnlyList<TaskSessionBody> Sessions,
    IReadOnlyList<string> Questions, bool? Answered, IReadOnlyList<TaskLinkBody> AnsweredBy, AnswerHint? Answer, PullRequestBody? PullRequest, CiBody? Ci = null);

public sealed record ArtifactRef(string Kind, string MediaType, string Sha256, string? Uri);

public sealed record BatchResultBody(string Status, string Summary, ErrorBody? Error, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, decimal? Usd);

public sealed record RateLimitBody(string BatchRunId, string TaskId, DateTimeOffset ObservedAt, DateTimeOffset ResetsAt);

/// <summary>The events of a batch as <c>run-status/v1</c>, without the result the last one carries. <paramref name="Available"/> is false for a batch with no records on disk that this process never held.
/// <paramref name="Source"/> is <c>records</c> (kept on disk, survive a restart) or <c>memory</c> (this process's).</summary>
public sealed record TimelineBody(bool Available, int Total, IReadOnlyList<RunStatus> Events, string? Source = null);

/// <summary><c>batch/v1</c>: one driven batch for the console's batch screen.</summary>
public sealed record BatchBody(string ContractVersion, RunSummary Run, string? Actor, BatchRepository? Repository, BatchLimitsBody Limits, string? CredentialDelivery, BatchResultBody? Result,
    JsonElement? Plan, JsonElement? Retro, IReadOnlyList<BatchTaskBody> Tasks, TimelineBody Timeline, IReadOnlyList<PullRequestBody> PullRequests, IReadOnlyList<ArtifactRef> Artifacts,
    IReadOnlyList<RateLimitBody> RateLimits, IReadOnlyList<string> NotRecorded);

/// <param name="Verification">The <c>verification</c> artifact: the command chargehand ran in a fresh container, its exit code, the end of its output and the test-integrity flag.</param>
/// <param name="SessionTests">What the session says it ran (<c>session-tests</c>); never merged with <paramref name="Verification"/>.</param>
/// <param name="Changes">The <c>changes</c> artifact: files with their line counts, and the diff (cut to the result's inline limit).</param>
/// <param name="Review">The research and review runs the session started (<c>review</c> artifact).</param>
/// <param name="KeptWork">What an unfinished task leaves for a continuation (<c>kept-work</c> artifact).</param>
public sealed record BatchTaskDetailBody(string ContractVersion, string BatchRunId, string? Actor, BatchRepository? Repository, BatchTaskBody Task, string? Summary, ErrorBody? ResultError,
    IReadOnlyList<ArtifactRef> Artifacts, JsonElement? Verification, JsonElement? SessionTests, JsonElement? Changes, JsonElement? Review, JsonElement? KeptWork, TimelineBody Timeline,
    IReadOnlyList<string> NotRecorded);

/// <summary>Builds <c>batch/v1</c> and <c>batch-task/v1</c> from the run log, the learning records, the saved state of unfinished batches and the events this process holds.</summary>
internal static class ConsoleBatches
{
    public const string BatchContract = "batch/v1";
    public const string TaskContract = "batch-task/v1";

    private const int GoalPreview = 500;
    private const int MaxEvents = 2000;

    internal static readonly IReadOnlyList<string> NotRecorded =
    [
        "Events are kept on disk per batch (at most 5000, a task's progress at most every 30 seconds) and pruned with the batch's other records past 500 batches or 180 days; a batch from before they were kept, or pruned, has only the events this process holds, if any.",
        "Sessions, governor decisions, checks and CI fix attempts are kept from the release that added them: an earlier task has the counts and its last session only.",
        "Only the end of a session's transcript is served (up to 256 KiB, while the session's output volume exists), on its own route; none of it is kept.",
        "A pull request's checks are the looks the integrator took while it watched them (until the pull request merged, closed, was given up on or the wait ended); one it never watched has none, and a look is kept only when it differs from the one before.",
    ];

    /// <summary>One task as the console's first look at the batch gets it: where the task stands, merged from the saved state, the batch result, the learning record and the task's own run.</summary>
    private sealed record Seed(string Id, string? Ref, string? Goal, string? Size, string? SizeFrom, IReadOnlyList<string> DependsOn, string? SplitFrom, ContinueFrom? Continues, TaskEntry? Live,
        JsonElement? Row);

    public static async Task<BatchBody?> BatchAsync(ConsoleContext c, Principal? who, string runId, int events, CancellationToken ct)
    {
        var index = await c.IndexAsync(ct);
        if (Locate(c, index, who, runId) is not { } located)
            return null;
        var (start, live) = located;
        var learning = (await c.LearningAsync(ct)).Where(e => e.Record.BatchId == runId).ToList();
        var batch = Assemble(c, index, start, live, learning);
        var kept = c.TaskRecordsOf(runId);
        var tasks = batch.Tasks.Select(t => TaskBody(c, index, who, batch, t, learning, kept, goalFull: false)).ToList();
        var timeline = Timeline(c, runId, null, events);
        var result = batch.Run?.Result;
        return new BatchBody(BatchContract, index.Summary(start), batch.Actor, batch.Repository, batch.Limits, batch.Delivery, result is null ? null : ResultBody(result),
            ResultArtifacts.Content(result, "plan"), ResultArtifacts.Content(result, "retro"), tasks, timeline, PullRequests(c, tasks, timeline, batch, learning),
            result?.Artifacts.Select(a => new ArtifactRef(a.Kind, a.MediaType, a.Sha256, a.Uri)).ToList() ?? [],
            [.. (c.Driven?.RateLimitPauses(c.Now) ?? []).Where(p => p.BatchRunId == runId).Select(p => new RateLimitBody(p.BatchRunId, p.TaskId, p.ObservedAt, p.ResetsAt))], NotRecorded);
    }

    public static async Task<BatchTaskDetailBody?> TaskAsync(ConsoleContext c, Principal? who, string runId, string taskId, int events, CancellationToken ct)
    {
        var index = await c.IndexAsync(ct);
        if (Locate(c, index, who, runId) is not { } located)
            return null;
        var (start, live) = located;
        var learning = (await c.LearningAsync(ct)).Where(e => e.Record.BatchId == runId).ToList();
        var batch = Assemble(c, index, start, live, learning);
        if (batch.Tasks.FirstOrDefault(t => t.Id == taskId) is not { } seed)
            return null;
        var body = TaskBody(c, index, who, batch, seed, learning, c.TaskRecordsOf(runId), goalFull: true);
        var timeline = Timeline(c, runId, taskId, events);
        var result = body.RunId is { } id ? index.RunOf(id)?.Result : null;
        var withIntegrator = body.PullRequest is { } pr ? body with { PullRequest = pr with { IntegratorLast = timeline.Events.LastOrDefault(e => e.Event == RunEventKind.Integrated)?.Detail } } : body;
        return new BatchTaskDetailBody(TaskContract, runId, batch.Actor, batch.Repository, withIntegrator, result?.Summary, result?.Error is { } e ? ErrorOf(e) : null,
            result?.Artifacts.Select(a => new ArtifactRef(a.Kind, a.MediaType, a.Sha256, a.Uri)).ToList() ?? [],
            ResultArtifacts.Content(result, "verification"), ResultArtifacts.Content(result, "session-tests"), ResultArtifacts.Content(result, "changes"),
            ResultArtifacts.Content(result, "review"), ResultArtifacts.Content(result, ContinuationSource.KeptWorkKind), timeline, NotRecorded);
    }

    /// <summary>Pull requests of the batches the caller sees, newest first; the same rows <c>GET /v1/pulls</c> lists, built from the learning records and the batch results.</summary>
    internal static PullRequestBody? PullRequestOf(ConsoleContext c, LearningEntry entry, string? integratorLast = null)
    {
        var r = entry.Record;
        if (r.Pr is not { } pr)
            return null;
        var mode = c.IntegrationOf(pr.Remote);
        var partial = r.Outcome == LearningOutcome.Partial;
        var state = entry.PrOutcome?.Outcome ?? PrOutcomes.Open;
        var (awaiting, reason) = Awaiting(state, mode, partial, r.TestIntegrityFlagged, r.At, c);
        return new PullRequestBody(pr.Url, pr.Number, RepositoryName(pr.Remote), r.BatchId, r.TaskId, r.RunId, state, entry.PrOutcome?.At, partial, r.TestIntegrityFlagged, ModeName(mode), awaiting, reason,
            integratorLast, r.Actor, r.At);
    }

    /// <summary>A pull request waits for a person when it is open and chargehand will not merge it: the repository's policy is <c>ask</c>, the change only touches tests or build files (ADR 0046), or the
    /// work is partial and stays a draft (ADR 0044). One that is older than the window the server keeps reading its outcome (<c>driven.learning.pr_days</c>) is no longer known to be open.</summary>
    private static (bool Awaiting, string? Reason) Awaiting(string state, IntegrationMode mode, bool partial, bool testIntegrity, DateTimeOffset at, ConsoleContext c)
    {
        var days = c.Profile?.Driven?.Learning?.PrDays ?? new DrivenLearning().PrDays;
        if (state != PrOutcomes.Open || c.Now - at > TimeSpan.FromDays(days))
            return (false, null);
        return testIntegrity ? (true, "test_integrity") : partial ? (true, "partial_draft") : mode == IntegrationMode.Ask ? (true, "policy_ask") : (false, null);
    }

    public static string ModeName(IntegrationMode mode) => mode switch { IntegrationMode.Merge => "merge", IntegrationMode.Ask => "ask", _ => "off" };

    public static string? RepositoryName(RemoteRepository? remote) =>
        remote is null ? null : remote.Host is null ? $"{remote.Owner}/{remote.Name}" : $"{remote.Host}/{remote.Owner}/{remote.Name}";

    public static ErrorBody ErrorOf(ResultError e) => new(JsonNamingPolicy.SnakeCaseLower.ConvertName(e.Code.ToString()), e.Message, e.Retryable, e.Action);

    private static BatchResultBody ResultBody(ResultContract r) =>
        new(JsonNamingPolicy.SnakeCaseLower.ConvertName(r.Status.ToString()), r.Summary, r.Error is { } e ? ErrorOf(e) : null, r.Usage.Input, r.Usage.Output, r.Usage.CacheRead, r.Usage.CacheWrite, r.Usage.Usd);

    /// <summary>The batch the caller may see: its start record and its saved state while unfinished. Null: unknown, not a batch, or not the caller's. A batch this process accepted but has not started has
    /// neither yet; <c>GET /v1/runs/{id}</c> answers for it.</summary>
    private static (StartRecord Start, BatchState? Live)? Locate(ConsoleContext c, RunIndex index, Principal? who, string runId)
    {
        if (!index.Starts.TryGetValue(runId, out var start) || start.Request.Driven is null || !Principal.Sees(who, start.Actor))
            return null;
        return (start, c.Driven?.LiveBatches().FirstOrDefault(b => b.RunId == runId));
    }

    private sealed record Assembled(string? Actor, BatchRepository? Repository, BatchLimitsBody Limits, string? Delivery, RunRecord? Run, BatchState? Live, IReadOnlyList<Seed> Tasks, IReadOnlyList<PlanEntry> Plan,
        string BatchRunId);

    private static Assembled Assemble(ConsoleContext c, RunIndex index, StartRecord start, BatchState? live, IReadOnlyList<LearningEntry> learning)
    {
        var request = start.Request;
        var run = index.RunOf(start.RunId);
        var driven = request.Driven;
        var repository = request.Context.Repository is { } r ? new BatchRepository(r.Path, r.Commit) : null;
        var limits = new BatchLimitsBody(driven?.MaxParallel, driven?.MaxTokensTotal, driven?.MaxUsdTotal, driven?.Plan);
        var batchArtifact = ResultArtifacts.Content(run?.Result, "driven-batch");
        var rows = batchArtifact is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty("tasks", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Where(x => ResultArtifacts.String(x, "id") is not null).ToList() : [];
        var plan = (live?.Plan ?? PlanOf(run?.Result))?.Tasks ?? [];
        DrivenTask? Asked(string id) => driven?.Tasks.FirstOrDefault(x => x.Id == id);
        PlanEntry? Planned(string id) => plan.FirstOrDefault(x => x.Id == id);

        List<Seed> seeds = [];
        if (live is not null)
            foreach (var e in live.Tasks)
                seeds.Add(new Seed(e.Id, e.Ref, e.Goal, e.Size ?? Asked(e.Id)?.Size, e.SizeFrom, e.DependsOn ?? Asked(e.Id)?.DependsOn ?? [], e.SplitFrom, Asked(e.Id)?.ContinueFrom, e, rows.FirstOrDefault(x => ResultArtifacts.String(x, "id") == e.Id)));
        else if (rows.Count > 0)
            foreach (var row in rows)
            {
                var id = ResultArtifacts.String(row, "id")!;
                var asked = Asked(id);
                var planned = Planned(id);
                seeds.Add(new Seed(id, asked?.Ref, null, asked?.Size ?? planned?.Size, planned?.SizeFrom ?? (asked?.Size is null ? null : "caller"), asked?.DependsOn ?? planned?.DependsOn ?? [], planned?.SplitFrom, asked?.ContinueFrom, null, row));
            }
        else
            foreach (var asked in driven?.Tasks ?? [])
                seeds.Add(new Seed(asked.Id, asked.Ref, asked.Goal, asked.Size, asked.Size is null ? null : "caller", asked.DependsOn ?? [], null, asked.ContinueFrom, null, null));
        return new Assembled(start.Actor, repository, limits, ResultArtifacts.String(batchArtifact, "credential_delivery"), run, live, seeds, plan, start.RunId);
    }

    private static BatchPlan? PlanOf(ResultContract? result)
    {
        var content = result?.Artifacts.FirstOrDefault(a => a.Kind == "plan")?.Content;
        if (content is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<BatchPlan>(content, JsonNamingOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonNamingOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };

    private static BatchTaskBody TaskBody(ConsoleContext c, RunIndex index, Principal? who, Assembled batch, Seed seed, IReadOnlyList<LearningEntry> learning, BatchTaskRecords kept, bool goalFull)
    {
        var row = seed.Row;
        var entry = seed.Live;
        var runId = entry?.RunId ?? ResultArtifacts.String(row, "run_id");
        var record = learning.Where(e => e.Record.TaskId == seed.Id).OrderBy(e => e.Record.At).LastOrDefault();
        var result = runId is null ? null : index.RunOf(runId)?.Result;
        var start = runId is not null && index.Starts.TryGetValue(runId, out var s) ? s : null;
        var goal = seed.Goal ?? entry?.SessionGoal ?? start?.Request.Text;
        var state = StateOf(entry, ResultArtifacts.String(row, "status"));
        var error = result?.Error is { } e ? ErrorOf(e) : ResultArtifacts.String(row, "error_code") is { } code ? new ErrorBody(code, ResultArtifacts.String(row, "detail") ?? "", false, null) : null;
        var detail = entry?.Outcome?.Detail ?? ResultArtifacts.String(row, "detail");
        var questions = result?.Status == ResultStatus.NeedsInput || state == "needs_input" ? (IReadOnlyList<string>)(result?.OpenQuestions ?? []) : [];
        // Whether it was answered is a fact about this task; which batch answered it is shown only if the caller may see that batch (the owner can continue an account's task).
        var continuations = state == "needs_input" ? index.ContinuationsOf(batch.BatchRunId, seed.Id).ToList() : [];
        var answeredBy = continuations.Where(x => Principal.Sees(who, x.Start.Actor)).Select(x => new TaskLinkBody(x.Start.RunId, x.Task.Id)).ToList();
        var branch = entry?.Outcome?.Branch ?? entry?.Branch ?? ResultArtifacts.String(row, "branch");
        var prUrl = entry?.Outcome?.PrUrl ?? ResultArtifacts.String(row, "pr_url");
        var spend = Spend(record, result);
        var pr = record is null ? PrFromRow(c, batch, seed, runId, prUrl, ResultArtifacts.Content(result, "pull-request"), start?.Started) : PullRequestOf(c, record);
        return new BatchTaskBody(seed.Id, seed.Ref, goal is null ? null : goalFull ? goal : ResultArtifacts.Cut(goal, GoalPreview), goal is not null && !goalFull && goal.Length > GoalPreview, seed.Size, seed.SizeFrom,
            seed.DependsOn, seed.SplitFrom, seed.Continues is { } from ? new TaskLinkBody(from.RunId, from.TaskId) : null, state, entry is null ? null : JsonNamingPolicy.SnakeCaseLower.ConvertName(entry.Phase.ToString()),
            entry?.Started ?? start?.Started, index.RunOf(runId ?? "")?.Finished, runId, entry?.Container, branch, prUrl, error, detail,
            record is null ? null : new BudgetBody(record.Record.Budget.Usd, record.Record.Budget.Source), spend,
            GovernorOf(record, kept.Governor.Where(g => g.TaskId == seed.Id).ToList()), record?.Record.Outcome, record?.Record.TestIntegrityFlagged ?? false,
            LiveOf(c, batch.BatchRunId, seed.Id, entry), Sessions(entry, runId, record, kept.Sessions.Where(s => s.TaskId == seed.Id)), questions, state == "needs_input" ? continuations.Count > 0 : null, answeredBy,
            state == "needs_input" && continuations.Count == 0 && runId is not null ? new AnswerHint("POST", "/v1/runs", new ContinueFrom(batch.BatchRunId, seed.Id), batch.Repository, "driven") : null, pr,
            CiOf(kept, seed.Id, goalFull));
    }

    /// <summary>The governor's counts (learning record) and its decisions in full (the sessions' logs); null when the task has neither.</summary>
    private static GovernorBody? GovernorOf(LearningEntry? record, List<GovernorRecord> logs)
    {
        var decisions = logs.SelectMany(g => g.Decisions.Select(d => (Session: g.RunId, Decision: d))).OrderBy(x => x.Decision.At).ToList();
        var log = decisions.Count == 0 && logs.Count == 0 ? null
            : (IReadOnlyList<GovernorDecisionBody>)[.. decisions.Select(x => new GovernorDecisionBody(x.Decision.At, x.Session, x.Decision.Turn, Snake(x.Decision.Mode), Snake(x.Decision.Action), x.Decision.Signal, x.Decision.Rule,
                x.Decision.Values, x.Decision.SpendUsd, x.Decision.BudgetUsd, x.Decision.SizeClass, x.Decision.Acted, x.Decision.BudgetSource))];
        if (record is not null)
            return new GovernorBody(record.Record.GovernorMode, record.Record.StopReason, record.Record.Governor, log, log is null ? null : logs.Sum(g => g.Total));
        if (log is null)
            return null;
        // A task that has not ended has no learning record yet: its counts come from the log.
        var counts = decisions.GroupBy(x => (x.Decision.Signal, Action: Snake(x.Decision.Action))).OrderBy(g => g.Key.Signal, StringComparer.Ordinal).ThenBy(g => g.Key.Action, StringComparer.Ordinal)
            .Select(g => new GovernorCount(g.Key.Signal, g.Key.Action, g.Count(), g.Count(x => x.Decision.Acted))).ToList();
        return new GovernorBody(decisions.Count > 0 ? Snake(decisions[0].Decision.Mode) : null, null, counts, log, logs.Sum(g => g.Total));
    }

    private static string Snake(Enum value) => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static CiBody? CiOf(BatchTaskRecords kept, string taskId, bool withHistory)
    {
        var looks = kept.Checks.Where(x => x.TaskId == taskId).OrderBy(x => x.At).Select(x => new CheckSnapshotBody(x.At, x.Pull, x.HeadSha, x.MergeStatus, x.Draft, x.AutoMerge, x.Decision,
            [.. x.Checks.Select(k => new CheckBody(k.Name, k.Required, k.Outcome, k.JobId))])).ToList();
        var attempts = kept.Attempts.Where(x => x.TaskId == taskId).OrderBy(x => x.Attempt).ThenBy(x => x.At)
            .Select(x => new CiAttemptBody(x.Attempt, x.MaxAttempts, x.At, x.JobsKey, x.Failing, x.Pushed, x.Detail)).ToList();
        return looks.Count == 0 && attempts.Count == 0 ? null : new CiBody(looks.LastOrDefault(), looks.Count, attempts, withHistory ? looks : null);
    }

    private static PullRequestBody? PrFromRow(ConsoleContext c, Assembled batch, Seed seed, string? runId, string? prUrl, JsonElement? artifact, DateTimeOffset? at)
    {
        if (prUrl is null)
            return null;
        return new PullRequestBody(prUrl, ResultArtifacts.Int(artifact, "number"), null, batch.BatchRunId, seed.Id, runId, "unknown", null, false, false, "off", false, null, null, batch.Actor, at);
    }

    private static string StateOf(TaskEntry? entry, string? rowStatus)
    {
        if (rowStatus is not null)
            return rowStatus;
        if (entry?.Outcome is { } outcome)
            return JsonNamingPolicy.SnakeCaseLower.ConvertName(outcome.State.ToString());
        return entry?.Phase switch
        {
            TaskPhase.Started or TaskPhase.Finished => "running",
            TaskPhase.HandedOver => "completed",
            TaskPhase.Failed => "failed",
            _ => "pending",
        };
    }

    private static TaskSpendBody? Spend(LearningEntry? record, ResultContract? result)
    {
        if (record is not null)
        {
            var s = record.Record.Spend;
            return new TaskSpendBody(result?.Usage.Usd, s.UsdEstimate, s.UsdEstimatePartial, s.InputTokens, s.OutputTokens, s.CacheReadTokens, s.CacheWriteTokens, s.Turns, s.Continuations);
        }
        if (result is not null)
            return new TaskSpendBody(result.Usage.Usd, null, false, result.Usage.Input, result.Usage.Output, result.Usage.CacheRead, result.Usage.CacheWrite, null, 0);
        return null;
    }

    /// <summary>The latest <c>session_progress</c> of the task that has numbers; null when the task is not running or this process holds no events for it.</summary>
    private static TaskLiveBody? LiveOf(ConsoleContext c, string batchRunId, string taskId, TaskEntry? entry)
    {
        if (entry is not { Phase: TaskPhase.Started or TaskPhase.Finished })
            return null;
        var latest = (c.Runs.Events(batchRunId) ?? c.EventsOf(batchRunId).Events)?.LastOrDefault(e => e.Event == RunEventKind.SessionProgress && e.TaskId == taskId && e.Tokens is not null);
        return latest is null ? null : new TaskLiveBody(latest.At ?? DateTimeOffset.UtcNow, latest.Stage, latest.Turns, latest.Tokens, latest.UsdEstimate, latest.UsdEstimatePartial ?? false, latest.CacheRead, latest.CacheWrite);
    }

    private static IReadOnlyList<TaskSessionBody> Sessions(TaskEntry? entry, string? runId, LearningEntry? record, IEnumerable<SessionRecord> kept)
    {
        var recorded = kept.OrderBy(s => s.Started).Select(s => new TaskSessionBody(s.RunId, s.Role, s.Runner, s.Started, s.Ended, s.Status, s.Reason, s.Turns, s.InputTokens, s.OutputTokens, s.CacheReadTokens,
            s.CacheWriteTokens, s.UsdEstimate, s.Usd, s.ExitCode, s.Commits)).ToList();
        if (recorded.Count > 0)
        {
            // A session still running has no record yet: the saved state names it.
            var known = recorded.Select(s => s.RunId).ToHashSet(StringComparer.Ordinal);
            return [.. recorded, .. (entry?.Runs ?? []).Select((r, i) => (Run: r, Index: i)).Where(x => !known.Contains(x.Run)).Select(x => new TaskSessionBody(x.Run, x.Index == 0 ? "first" : "continuation"))];
        }
        if (entry?.Runs is { Count: > 0 } runs)
            return [.. runs.Select((r, i) => new TaskSessionBody(r, i == 0 ? "first" : "continuation"))];
        if (runId is null)
            return [];
        return [new TaskSessionBody(runId, record?.Record.Spend.Continuations > 0 ? "last" : "first")];
    }

    private static TimelineBody Timeline(ConsoleContext c, string batchRunId, string? taskId, int limit)
    {
        if (c.EventsOf(batchRunId) is not ({ } all, var source))
            return new TimelineBody(false, 0, []);
        var wanted = taskId is null ? all : [.. all.Where(e => e.TaskId == taskId)];
        var kept = wanted.Skip(Math.Max(0, wanted.Count - Math.Clamp(limit, 0, MaxEvents))).Select(e => e with { Result = null }).ToList();
        return new TimelineBody(true, wanted.Count, kept, source);
    }

    private static IReadOnlyList<PullRequestBody> PullRequests(ConsoleContext c, IReadOnlyList<BatchTaskBody> tasks, TimelineBody timeline, Assembled batch, IReadOnlyList<LearningEntry> learning) =>
    [
        .. tasks.Select(t => t.PullRequest).OfType<PullRequestBody>()
            .Select(p => p with { IntegratorLast = timeline.Events.LastOrDefault(e => e.Event == RunEventKind.Integrated && e.TaskId == p.TaskId)?.Detail }),
    ];
}
