using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Core;

namespace Chargehand.Modules.Tasks.Api;

/// <summary>The task's end: the handover, the task's own result in the run log, and on an opted-in repository the follow-up (ADR 0046).</summary>
public sealed partial class TaskRunner
{
    private async Task<TaskOutcome> FinishAsync(ResolvedTask task, TaskRun run, IProgress<TaskUsage> progress, CancellationToken ct)
    {
        var (partial, handoverRun, handed) = await HandOverTaskAsync(task, run, ct);
        var result = await ResultAsync(task, run, partial, handoverRun.RunId, handed, ct);
        var startNote = run.StartFailure is null ? null : $"; continuation {run.Continuation + 1} did not start";
        // A stall that would continue (ADR 0044) but left no commit has no branch to continue from; a re-plan (ADR 0049's chain, issue #432) replaces the plain continuation attempt.
        var replan = run.Best is null && run.Session.Status == SessionStatus.Stalled && StallDetector.Continuable(run.Session.Reason);
        (string? Detail, SessionOutcome? Spent) follow = (null, null);
        if (FollowsUp(handed, run.Stack))
        {
            // A restart during the follow-up keeps this outcome rather than handing the task over again; the pull request itself holds the count of fix attempts (ADR 0046).
            var early = Outcome(task, run.RunId, result, run.Total, handed, settings.Priced, partial, startNote, replan);
            await LearnAsync(task, early, run.Session, run.Total, run.Continuation, run.Decisions, handed, partial, run.LastHead, run.Budget);
            await RecordAsync(task.Id, new TaskFinished(early));
            follow = await FollowUpAsync(task, handed!, Path.Combine(handoverRun.OutDirectory, "chargehand.bundle"), run.Best?.Commits ?? 0, run.Branch, partial, run.Total, run.Containers, run.Span,
                run.TraceId, progress, sha => run.LastHead = sha, run.Budget, ct);
        }
        var spent = follow.Spent ?? run.Total;
        var outcome = Outcome(task, run.RunId, result, spent, handed, settings.Priced, partial, startNote, replan);
        if (follow.Detail is { } integration)
            outcome = outcome with { Detail = outcome.Detail is { } before ? $"{before}; {integration}" : integration };
        await LearnAsync(task, outcome, run.Session, spent, run.Continuation, run.Decisions, handed, partial, run.LastHead, run.Budget);
        await RecordAsync(task.Id, new TaskFinished(outcome));
        run.Ended = true;
        run.Span?.SetTag("chargehand.task.continuations", run.Continuation);
        Tag(run.Span, outcome, spent, run.Started);
        return outcome;
    }

    /// <summary>A finished session's branch as a pull request; an unfinished one's work as a partial draft, from the latest session whose bundle holds commits.</summary>
    private async Task<(bool Partial, (string RunId, string OutDirectory) Run, HandoverOutcome? Handed)> HandOverTaskAsync(ResolvedTask task, TaskRun run, CancellationToken ct)
    {
        var (session, best) = (run.Session, run.Best);
        if (Bare)
            return await HandOverBareAsync(task, run, ct);
        var finished = session.Status == SessionStatus.Completed && run.ReportJson is not null && DrivenReport.Parse(run.ReportJson) is not null;
        var partial = !finished && best is not null && (Salvageable(session) || session.Status == SessionStatus.NeedsInput || best.RunId != run.RunId);
        var handoverRun = partial ? (best!.RunId, best.OutDirectory) : (run.RunId, run.OutDirectory);
        HandoverOutcome? handed = null;
        if (finished)
            handed = await HandOverAsync(task, handoverRun, run.Branch, PullRequestTitle.Of(task.Title, task.Goal), $"{DrivenReport.Parse(run.ReportJson!)!.Summary}\n\nTask: {task.Id}{ContinuationNote(run.Continuation)}{GoalSection(task.Goal)}{CostReceipt.Footer(run.Total, run.Continuation)}", partial: false, run.Stack, session, ct);
        else if (partial)
        {
            await RestoreBundleAsync(handoverRun.Item1, ct);
            handed = await HandOverAsync(task, handoverRun, run.Branch, $"[partial] {PullRequestTitle.Of(task.Title, task.Goal)}", PartialBody(task, session, run.Total, run.Continuation, run.StartFailure, ReadProgress(run.OutDirectory)), partial: true, run.Stack, run.Total, ct);
        }
        return (partial, handoverRun, handed);
    }

    /// <summary>The bare arm's end (ADR 0080): a session that finished with commits has its branch pushed; any other end leaves nothing, with no partial draft and no second session.</summary>
    private async Task<(bool Partial, (string RunId, string OutDirectory) Run, HandoverOutcome? Handed)> HandOverBareAsync(ResolvedTask task, TaskRun run, CancellationToken ct)
    {
        var session = run.Session;
        var handoverRun = (run.RunId, run.OutDirectory);
        if (session is not { Status: SessionStatus.Completed, BundleWritten: true, Commits: > 0 })
            return (false, handoverRun, null);
        var handed = await HandOverAsync(task, handoverRun, run.Branch, PullRequestTitle.Of(task.Title, task.Goal), $"Task: {task.Id}", partial: false, run.Stack, session, ct, bare: true);
        return (false, handoverRun, handed);
    }

    /// <summary>The task's own <c>result/v1</c>, built, checked and signed, and its start and run records in the run log.</summary>
    private async Task<ResultContract> ResultAsync(ResolvedTask task, TaskRun run, bool partial, string handoverRun, HandoverOutcome? handed, CancellationToken ct)
    {
        run.LastHead = handed is { Status: HandoverStatus.Pushed or HandoverStatus.PrFailed } ? handed.Commit : null;
        var scratchClone = Path.Combine(settings.ScratchRoot, handoverRun);
        // Once the branch is pushed, a cancel no longer undoes the task: its result is recorded with the pull request.
        var record = run.LastHead is null ? ct : CancellationToken.None;
        var children = (await log.ListAsync(new RunListQuery(Since: run.Started, Limit: 200), record)).Where(r => r.ParentRunId is { } parent && run.Runs.Contains(parent)).ToList();
        var result = await results.BuildAsync(new DrivenTaskInput(task.Id, run.RunId, run.TraceId, settings.Model, settings.ClaudeVersion, run.Total, run.ReportJson, handed, children,
            Directory.Exists(scratchClone) ? scratchClone : settings.SourcePath, settings.BaseCommit, settings.Priced,
            partial ? new KeptWork(run.Branch, run.Best!.RunId, run.Best.Commits, settings.BaseCommit) : null, Bare), record);
        result = result with { Actor = settings.Actor };
        result = results.Sign(result);
        await log.AppendAsync(new StartRecord(run.RunId, run.Started, run.TraceId, settings.Request with { Text = task.Goal, Driven = null }, Environment.ProcessId, settings.BatchId, ProcessBoot.Id, settings.Actor), CancellationToken.None);
        await log.AppendAsync(new RunRecord(run.RunId, run.Started, DateTimeOffset.UtcNow, settings.Request.Context.Preset, null, null, result, Actor: settings.Actor), CancellationToken.None);
        return result;
    }

    /// <summary>The task's pull request is on a repository the profile opted in to integration (ADR 0046). A task that depends on others is left alone: merging its pull request by policy
    /// would merge the work it was stacked on as well, before anyone has read that.</summary>
    private bool FollowsUp(HandoverOutcome? handed, StackBase? stack) =>
        stack is null && settings.Integration is { } integrator && handed?.PullRequest is not null && integrator.Remote(settings.RemoteUrl) is { } repository && integrator.Mode(repository) != IntegrationMode.Off;

    /// <summary>After the handover, on an opted-in repository: mark the draft ready and merge it by policy, then watch its required checks and, when some fail, run short fix sessions on the same branch
    /// seeded from the last bundle (<see cref="CiFixSession"/>). The pull request exists whatever happens here, so a GitHub error or a cancel ends in a line of detail, never in a failed task.</summary>
    /// <returns>The line for the task outcome, and the usage of the fix sessions added to the task's (null: none ran).</returns>
    private async Task<(string? Detail, SessionOutcome? Spent)> FollowUpAsync(ResolvedTask task, HandoverOutcome handed, string seedBundle, int seededCommits, string branch, bool partial, SessionOutcome total,
        Dictionary<string, string?> containers, Activity? span, string traceId, IProgress<TaskUsage> progress, Action<string> headMoved, BudgetChoice? budget, CancellationToken ct)
    {
        var integrator = settings.Integration!;
        var pr = handed.PullRequest!;
        var target = new IntegrationTarget(integrator.Remote(settings.RemoteUrl)!, pr.Number, branch, partial, task.Goal, handed.TestIntegrity?.Reason, settings.LocalOnly);
        void Report(string detail) => Publish(task.Id, RunEventKind.Integrated, detail, s => s with { PrUrl = pr.Url, Branch = branch });
        var fixes = new CiFixes
        {
            Integrator = integrator,
            Branch = branch,
            Total = total,
            Containers = containers,
            Span = span,
            TraceId = traceId,
            Progress = progress,
            HeadMoved = headMoved,
            Budget = budget,
            Seed = seedBundle,
            SeededCommits = seededCommits,
        };
        var watch = WatchOf(task, pr);
        try
        {
            var first = await integrator.IntegrateAsync(target, Report, ct, headMoved);
            var detail = $"integration: {first.Detail}";
            if (first.Action is IntegrationAction.Asked or IntegrationAction.AutoMergeEnabled)
            {
                var last = await integrator.FollowAsync(target, (request, token) => FixAsync(task, fixes, request, token), Report, ct, headMoved, watch);
                detail += $"; {last.Detail}{(fixes.Fixes > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({fixes.Fixes} CI fix attempt(s) pushed)") : "")}";
            }
            return (detail, fixes.Sum is null ? null : Total(fixes.Sum, total));
        }
        catch (Exception e) when (IsStartFailure(e, ct) || e is OperationCanceledException && ct.IsCancellationRequested)
        {
            Report($"integration ended early: {(e is OperationCanceledException ? "cancelled" : e.Message)}");
            return ($"integration ended early: {(e is OperationCanceledException ? "cancelled" : e.Message)}", fixes.Sum is null ? null : Total(fixes.Sum, total));
        }
    }

    /// <summary>A follow-up's CI fix sessions: what the next one starts from, and what they have spent and pushed so far.</summary>
    private sealed class CiFixes
    {
        public required Integrator Integrator { get; init; }

        public required string Branch { get; init; }

        public required SessionOutcome Total { get; init; }

        public required Dictionary<string, string?> Containers { get; init; }

        public Activity? Span { get; init; }

        public required string TraceId { get; init; }

        public required IProgress<TaskUsage> Progress { get; init; }

        public required Action<string> HeadMoved { get; init; }

        public BudgetChoice? Budget { get; init; }

        public required string Seed { get; set; }

        public int SeededCommits { get; set; }

        public SessionOutcome? Sum { get; set; }

        public int Fixes { get; set; }
    }

    private async Task<CiFixResult> FixAsync(ResolvedTask task, CiFixes f, CiFixRequest request, CancellationToken token)
    {
        var running = f.Sum is null ? f.Total : Total(f.Sum, f.Total);
        var (tokensLeft, usdLeft) = Remaining(running);
        if (tokensLeft <= 0 || usdLeft is <= 0)
            return new CiFixResult(false, "the task's token or cost cap is spent");
        if (!File.Exists(f.Seed))
            return new CiFixResult(false, "the branch's bundle is gone, so a session cannot start from it");
        var fixRun = RunIds.New();
        f.Containers[fixRun] = null;
        var outDirectory = Path.Combine(settings.ScratchRoot, "out", fixRun);
        Directory.CreateDirectory(outDirectory);
        var session = await FixSessionAsync(task, f, request, fixRun, outDirectory, running, (tokensLeft, usdLeft), token);
        f.Sum = f.Sum is null ? session : Total(f.Sum, session);
        f.Progress.Report(Capped(f.Sum.InputTokens + f.Sum.OutputTokens + f.Total.InputTokens + f.Total.OutputTokens, settings.Priced ? (f.Sum.CostUsd ?? 0) + (f.Total.CostUsd ?? 0) : 0, ended: true));
        if (session is not { Status: SessionStatus.Completed, BundleWritten: true } || session.Commits <= f.SeededCommits)
            return new CiFixResult(false, session.Status == SessionStatus.Completed ? "the session made no new commit" : $"the session ended {StopReason(session)}");
        var fixHandover = await FixHandOverAsync(task, f, request, fixRun, outDirectory, session, token);
        if (fixHandover.Status == HandoverStatus.NotPushed)
            return new CiFixResult(false, $"not pushed: {fixHandover.Reason}");
        (f.Seed, f.SeededCommits) = (Path.Combine(outDirectory, "chargehand.bundle"), session.Commits);
        if (fixHandover.Commit is { } pushedHead)
            f.HeadMoved(pushedHead);
        f.Fixes++;
        return new CiFixResult(true, "chargehand's own tests passed and the commits are on the branch");
    }

    /// <summary>One CI fix session, from its start to its outcome, recorded for the console.</summary>
    private async Task<SessionOutcome> FixSessionAsync(ResolvedTask task, CiFixes f, CiFixRequest request, string fixRun, string outDirectory, SessionOutcome running, (long Tokens, decimal? Usd) left,
        CancellationToken token)
    {
        var integrator = f.Integrator;
        var sessionStarted = DateTimeOffset.UtcNow;
        SessionOutcome session;
        try
        {
            var container = await StartSessionAsync(task, fixRun, f.Branch, request.Goal, integrator.CiFix.Turns, integrator.CiFix.Minutes, left, running.UsdEstimate ?? 0, f.Seed, sessionStarted,
                $"session container started (CI fix attempt {request.Attempt} of {request.MaxAttempts})", f.Span, f.TraceId, f.Budget, token, followUp: true);
            f.Containers[fixRun] = container;
            await WaitAsync(container, fixRun, task.Id, integrator.CiFix.Minutes, f.Progress, sessionStarted, running, token);
            await Tolerant(task.Id, () => volumes.FetchOutputAsync(fixRun, outDirectory, token), token);
            session = SessionDriver.ReadOutcome(outDirectory) ?? new SessionOutcome(SessionStatus.Failed, "no_outcome", 0, 0, 0, 0, null, null, [], false);
        }
        catch (Exception e)
        {
            RecordCutShort(task, (fixRun, sessionStarted, "ci_fix"), e);
            throw;
        }
        RecordSession(task, "ci_fix", fixRun, sessionStarted, DateTimeOffset.UtcNow, session);
        RecordGovernor(task, fixRun, outDirectory, session);
        return session;
    }

    private Task<HandoverOutcome> FixHandOverAsync(ResolvedTask task, CiFixes f, CiFixRequest request, string fixRun, string outDirectory, SessionOutcome spent, CancellationToken token) =>
        handover.RunAsync(new HandoverInput(fixRun, outDirectory, settings.SourcePath, settings.BaseCommit, f.Branch, settings.RemoteUrl, settings.BaseBranch, PullRequestTitle.Of(task.Title, task.Goal),
            $"CI fix attempt {request.Attempt}", settings.ScratchRoot, settings.Push, settings.Request.Context.Verify, [.. settings.ModelEnvironment.Values], AllowLocalRemote: settings.AllowLocalRemote, LocalOnly: settings.LocalOnly,
            Report: e => { if (e.Kind != RunEventKind.PrOpened) Publish(task.Id, e.Kind, e.Detail, s => s with { Branch = e.Kind == RunEventKind.Pushed ? f.Branch : null }); },
            Provenance: TaskProvenance.Of(task, fixRun, spent, settings.Model, settings.ClaudeVersion)), token);

    /// <summary>The pull request's checks and the CI fix attempts, kept for the console (CHARGEHAND-214).</summary>
    private IntegrationWatch WatchOf(ResolvedTask task, PullRequestRef pr)
    {
        string? lastChecks = null;
        return new IntegrationWatch(
            o =>
            {
                // A look at the checks is kept when it differs from the one before.
                var key = $"{o.HeadSha}|{o.MergeStatus}|{o.Decision}|{string.Join(',', o.Checks.Select(c => $"{c.Name}:{c.Required}:{c.Outcome}"))}";
                if (key == lastChecks)
                    return;
                lastChecks = key;
                records?.Append(settings.BatchId, new ChecksRecord(task.Id, o.At, pr.Number, o.HeadSha, o.MergeStatus, o.IsDraft, o.AutoMerge, o.Decision,
                    [.. o.Checks.Select(c => new CheckRecord(c.Name, c.Required, JsonNamingPolicy.SnakeCaseLower.ConvertName(c.Outcome.ToString()), c.JobId))]));
            },
            a => records?.Append(settings.BatchId, new CiAttemptRecord(task.Id, a.Attempt, a.MaxAttempts, a.At, a.JobsKey, a.Failing, a.Pushed, a.Detail)));
    }

    /// <summary>A continuation's start that failed for a reason outside the task (the runner is full or unreachable, the seed was refused, the call timed out) must not cost the work
    /// the earlier sessions left. A cancel by the caller or a stopping server is not one.</summary>
    private static bool IsStartFailure(Exception e, CancellationToken ct) =>
        e is ChargehandException or HttpRequestException or IOException || (e is TaskCanceledException && !ct.IsCancellationRequested);

    private Task<HandoverOutcome> HandOverAsync(ResolvedTask task, (string RunId, string OutDirectory) run, string branch, string title, string body, bool partial, StackBase? stack, SessionOutcome spent, CancellationToken ct, bool bare = false) =>
        handover.RunAsync(new HandoverInput(run.RunId, run.OutDirectory, settings.SourcePath, settings.BaseCommit, branch, settings.RemoteUrl, stack?.PullRequestBase ?? settings.BaseBranch,
            title, stack is null ? body : $"{body}\n\n{stack.Note}", settings.ScratchRoot, settings.Push, settings.Request.Context.Verify, [.. settings.ModelEnvironment.Values], AllowLocalRemote: settings.AllowLocalRemote, LocalOnly: settings.LocalOnly,
            Report: e => Publish(task.Id, e.Kind, e.Detail, s => s with { PrUrl = e.PrUrl, Branch = e.Kind == RunEventKind.Pushed ? branch : null }), Partial: partial,
            Goal: task.Goal, UpdateReused: !partial && task.From?.Pushed is null, PolicyNote: stack is null && settings.Integration is { } integrator && integrator.Remote(settings.RemoteUrl) is { } repository ? integrator.Describe(repository, partial) : null,
            FallbackBaseBranch: stack?.PullRequestBase is null ? null : settings.BaseBranch,
            Provenance: TaskProvenance.Of(task, run.RunId, spent, settings.Model, settings.ClaudeVersion), Bare: bare), ct);

    /// <summary>How much of a handoff goes into a continuation's prompt, and how much into a partial pull request's body.</summary>
    private const int ProgressInGoal = 8000;
    private const int ProgressInBody = 6000;

    /// <summary>The <c>progress.md</c> a stopped session left, or null: an older session image writes none, and a session that was not stopped by the driver has none.</summary>
    private static string? ReadProgress(string directory)
    {
        var file = Path.Combine(directory, ContainerTemplate.ProgressFile);
        try
        {
            return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } text ? text : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private const int GoalInBody = 12000;

    /// <summary>The whole goal, for the pull request body: the title is only its first sentence.</summary>
    private static string GoalSection(string goal) => "\n\nGoal:\n\n" + (goal.Length <= GoalInBody ? goal.Trim() : goal[..GoalInBody].TrimEnd() + "\n[goal truncated]");

    private static string Truncated(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "\n\n[handoff truncated]";

    private static string PartialBody(ResolvedTask task, SessionOutcome last, SessionOutcome total, int continuation, string? startFailure, string? progress)
    {
        var turns = total.Turns;
        var lead = last.Status == SessionStatus.Completed
            ? $"the session ended after {turns} turns but printed no usable report, even when asked for one, so what it did is unchecked."
            : $"the session {(last.Status == SessionStatus.NeedsInput ? "stopped to ask a question" : $"was stopped by {StopReason(last).Replace('_', ' ')}")} after {turns} turns and did not finish.";
        var body = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"Partial result: {lead} This is unfinished work, kept so it is not lost; read it before anything else.\n\nTask: {task.Id}"))
            .Append(ContinuationNote(continuation));
        if (last.Status == SessionStatus.NeedsInput && last.Questions.Count > 0)
            body.Append("\n\nQuestions for you:\n").AppendJoin('\n', last.Questions.Select(q => $"- {q}"));
        if (startFailure is not null)
            body.Append("\n\nA further session could not be started: ").Append(startFailure).Append('.');
        if (last.SnapshotNote is { Length: > 0 } note)
            body.Append("\n\nNot everything the session wrote is on this branch: ").Append(note).Append('.');
        body.Append(GoalSection(task.Goal));
        if (progress is not null)
            body.Append("\n\n---\n\n").Append(Truncated(progress, ProgressInBody));
        return body.Append(CostReceipt.Footer(total, continuation)).ToString();
    }

    private static TaskOutcome Outcome(ResolvedTask task, string runId, ResultContract result, SessionOutcome session, HandoverOutcome? handed, bool priced, bool partial, string? partialNote,
        bool replan = false)
    {
        var state = result.Status switch
        {
            ResultStatus.Completed => TaskState.Completed,
            ResultStatus.NeedsInput => TaskState.NeedsInput,
            _ => TaskState.Failed,
        };
        var pushed = handed?.Status is HandoverStatus.Pushed or HandoverStatus.PrFailed;
        var detail = Detail(state, result, session, handed, pushed, partial, partialNote);
        if (replan)
            detail = detail is { Length: > 0 } ? $"{detail}; stalled with no commits, re-planning into smaller tasks" : "stalled with no commits, re-planning into smaller tasks";
        return new TaskOutcome(task.Id, state, runId, detail, result.Error?.Code,
            priced ? session.CostUsd ?? 0 : 0, session.InputTokens + session.OutputTokens, pushed ? handed!.Branch : null, handed?.PullRequest?.Url,
            session.Status == SessionStatus.RateLimited ? session.RateLimitResetsAt : null, Replan: replan);
    }

    private static string? Detail(TaskState state, ResultContract result, SessionOutcome session, HandoverOutcome? handed, bool pushed, bool partial, string? partialNote) =>
        state == TaskState.Completed ? handed?.TestIntegrity is { } flag ? $"test integrity: {flag.Reason}" : null
            : !partial || handed is null ? result.Summary
            : session.Status == SessionStatus.NeedsInput ? $"partial: needs input ({string.Join("; ", session.Questions)}); {(pushed ? handed.PullRequest is null ? "branch pushed, pull request failed" : "draft PR opened" : $"not handed over: {handed.Reason}")}{partialNote}"
            : pushed ? $"partial: {EndedBy(session)}; {(handed.PullRequest is null ? "branch pushed, pull request failed" : "draft PR opened")}{partialNote}"
            : $"partial: {EndedBy(session)}; not handed over: {handed.Reason}{partialNote}";

    private static string EndedBy(SessionOutcome session) => session.Status == SessionStatus.Completed ? "finished without a usable report" : $"stopped by {StopReason(session)}";
}
