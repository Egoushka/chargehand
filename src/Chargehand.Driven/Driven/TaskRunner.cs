using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Chargehand.Budget;
using Chargehand.Config;
using Chargehand.Containers;
using Chargehand.Contracts;
using Chargehand.Egress;
using Chargehand.Results;
using Chargehand.RunLog;
using Chargehand.Runtime;
using Chargehand.Verification;

namespace Chargehand.Driven;

/// <summary>How a session's output volume is written and read (ADR 0039): the task file goes in before the container starts, and after it ends the bundle, the report
/// and the outcome come out into a directory chargehand owns.</summary>
public interface ISessionVolumes
{
    Task WriteTaskAsync(string runId, SessionTask task, CancellationToken ct);

    /// <summary>Puts the previous session's bundle into the volume as <c>seed.bundle</c>; the continuation session starts its branch from it (ADR 0044).</summary>
    Task WriteSeedAsync(string runId, string bundlePath, CancellationToken ct);

    /// <summary>Copies <c>chargehand.bundle</c>, <c>driven-report.json</c>, <c>session-outcome.json</c> and a stopped session's <c>handoff.json</c> and <c>progress.md</c> (each only if it exists;
    /// a runner too old to know the last two is not an error) from the volume into <paramref name="directory"/>.</summary>
    Task FetchOutputAsync(string runId, string directory, CancellationToken ct);

    /// <summary>The session's running tally while it works; null if it has not written one yet or the file is unreadable.</summary>
    Task<TaskUsage?> ReadUsageAsync(string runId, CancellationToken ct);
}

/// <summary>Used where the engine cannot move files through a volume: a batch then ends each task as <c>container_unavailable</c> before any session container starts.</summary>
public sealed class UnavailableSessionVolumes : ISessionVolumes
{
    private static ChargehandException Unavailable() => new(ErrorCode.ContainerUnavailable, "this deployment cannot put a task into a session's output volume or read its bundle out",
        "Set driven.runner in the profile, or install Docker, and list the session image in driven.images (docs/guide/driven.md).");

    public Task WriteTaskAsync(string runId, SessionTask task, CancellationToken ct) => throw Unavailable();

    public Task WriteSeedAsync(string runId, string bundlePath, CancellationToken ct) => throw Unavailable();

    public Task FetchOutputAsync(string runId, string directory, CancellationToken ct) => throw Unavailable();

    public Task<TaskUsage?> ReadUsageAsync(string runId, CancellationToken ct) => throw Unavailable();
}

/// <summary>What a session's run token is for: one repository at one commit, within the task's caps, until it expires.</summary>
public sealed record TaskGrant(string RunId, string RepositoryPath, string Commit, decimal? MaxUsd, long? MaxTokens, DateTimeOffset Expires);

public delegate string TaskTokenMinter(TaskGrant grant);

/// <param name="Repository">The request's repository; its commit is what a session's run token is limited to.</param>
/// <param name="SourcePath">chargehand's own checkout of that commit: the workspace's read-only source and the handover's scratch clone source.</param>
/// <param name="ModelEnvironment">The real model credential by environment name. It is the session container's environment only when <paramref name="GatewayKey"/> is null (the fallback
/// delivery, recorded as such on the batch result); always the list of values the handover scans for.</param>
/// <param name="GatewayKey">Set, the egress container exchanges run tokens (ADR 0039, decision 9): a session gets a token minted with this key and the model endpoint's address, and no real credential.</param>
/// <param name="McpUrl">The chargehand server's MCP address on the batch network; null: the session gets no research or review calls.</param>
/// <param name="ModelBaseUrl">The session's <c>ANTHROPIC_BASE_URL</c> when it reaches the model through a gateway; null: Anthropic directly.</param>
/// <param name="CallbackForwards">Some address the session uses is a forward on the egress container (plain HTTP), which must bypass the proxy.</param>
/// <param name="Otlp">Where the session's Claude Code exports its own logs and metrics; null: it exports nothing.</param>
/// <param name="Prices">The profile's price table, for the session span's cost.</param>
/// <param name="UsageOnSpans">The profile's <c>telemetry.usage_on_spans</c> (ADR 0021).</param>
/// <param name="Governor">The profile's <c>driven.governor</c> (ADR 0045); null: no governor in the sessions.</param>
/// <param name="Integration">The profile's <c>driven.integrator</c> (ADR 0046): marks a finished task's pull request ready, merges it by policy and fixes failing CI; null: the draft is left alone.</param>
/// <param name="Shared">The batch's cache volumes and test services and the repository facts for the session's environment note (ADR 0048); null: the note has the image's facts only.</param>
public sealed record TaskRunnerSettings(string BatchId, RunRequest Request, string Image, string SourcePath, string BaseCommit, string RemoteUrl, string BaseBranch,
    BatchNetworkInfo Network, string? McpUrl, DrivenPreset Preset, TaskLimits Limits, bool Priced, IReadOnlyDictionary<string, string> ModelEnvironment, PushCredential Push,
    string ScratchRoot, string Model, string ClaudeVersion, bool AllowLocalRemote = false, string? ModelBaseUrl = null, bool CallbackForwards = false, DrivenOtlpRoute? Otlp = null,
    IReadOnlyDictionary<string, ModelPrice>? Prices = null, bool UsageOnSpans = false, string? GatewayKey = null, DrivenGovernor? Governor = null, Integrator? Integration = null, SharedServicesPlan? Shared = null);

/// <summary>Runs one task of a batch to its end (ADR 0039): a workspace at the pinned commit, a session container on the batch network, then the handover (chargehand's own
/// verification, the scan, the push, a draft pull request) and the task's own <c>result/v1</c> in the run log. A cancel signals the session and ends the task; it never reaches
/// the handover, so nothing is pushed. <paramref name="publish"/> gets the task's steps as events of the batch run: <c>container_started</c>, <c>session_progress</c> when the
/// session's tally changes, and the handover's <c>verify_finished</c>, <c>pushed</c> and <c>pr_opened</c>. With a <paramref name="journal"/> the task's progress is saved, and a task it shows as begun is taken up where it
/// stood after a server restart (ADR 0043). <paramref name="detaching"/> says the server is stopping rather than the run being cancelled: the session container is then left running for the next process.</summary>
public sealed class TaskRunner(IContainerEngine engine, IWorkspaceEngine workspace, ISessionVolumes volumes, IBranchHandover handover, IRunLog log, TaskTokenMinter mint,
    TaskRunnerSettings settings, IEvidenceResolver resolver,
    Func<ResultContract, EvidenceScope, CancellationToken, Task<ResultContract>>? supportCheck, Func<ResultContract, ResultContract>? sign,
    TimeSpan? poll = null, TimeSpan? stopGrace = null, Action<RunStatus>? publish = null, BatchJournal? journal = null, Func<bool>? detaching = null) : ITaskRunner
{
    private static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultUsagePoll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WallClockSlack = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions OutcomeJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record BestRun(string RunId, string OutDirectory, int Commits);

    public async Task<TaskOutcome> RunAsync(ResolvedTask task, IProgress<TaskUsage> progress, CancellationToken ct)
    {
        var entry = journal?.Entry(task.Id);
        if (entry is { Phase: TaskPhase.HandedOver or TaskPhase.Failed, Outcome: { } done })
            return done;
        var resumed = entry is { Phase: TaskPhase.Started or TaskPhase.Finished, RunId: not null } ? entry : null;
        string? restartSeed = null;
        if (resumed is not null && await GoneWithoutOutputAsync(resumed, ct))
        {
            // Nothing to take up. A continuation that never started is started again from the previous session's bundle (ADR 0044); any other task begins again
            // under a new run id and branch. The old attempt's leftovers are cleared either way.
            await RemoveRunAsync(resumed.RunId!, resumed.Container);
            restartSeed = resumed.Continuation > 0 && resumed.Runs is { Count: > 1 } known ? await RestoreBundleAsync(known[^2], ct) : null;
            if (restartSeed is null)
                resumed = null;
        }
        var attach = resumed is not null && restartSeed is null;
        var runId = resumed?.RunId ?? Orchestrator.NewRunId();
        var branch = resumed?.Branch ?? task.From?.Kept?.Branch ?? $"chargehand/{runId}";
        var started = resumed?.Started ?? DateTimeOffset.UtcNow;
        var preset = settings.Preset;
        var maxTurns = task.MaxTurns ?? preset.MaxTurns;
        var maxMinutes = task.MaxMinutes ?? preset.MaxMinutes;
        // Sessions after the first (ADR 0044): the earlier ones' usage added up, and each one's run id, the first one's first (the branch is named after it).
        var continuation = resumed?.Continuation ?? 0;
        var prior = resumed?.Prior;
        List<string> runs = [.. resumed?.Runs ?? [runId]];
        if (restartSeed is not null)
        {
            // The same session again under a new run id: the name of the one that never started is not reused.
            runId = Orchestrator.NewRunId();
            runs[^1] = runId;
            var (again, allRuns) = (runId, runs.ToList());
            journal?.Update(task.Id, e => e with { RunId = again, Runs = allRuns, Container = null });
        }
        // A task that continues an earlier one starts with that task's bundle as its best, so a session that adds nothing to it neither continues nor loses it (ADR 0045).
        var best = (resumed?.Best ?? (resumed is null && task.From?.Kept is { } carried ? new BestBundle(carried.BundleRun, carried.Commits) : null)) is { } kept
            ? new BestRun(kept.RunId, Path.Combine(settings.ScratchRoot, "out", kept.RunId), kept.Commits) : null;
        var containers = new Dictionary<string, string?>();
        var outDirectory = Path.Combine(settings.ScratchRoot, "out", runId);
        string? seed = restartSeed;
        var goal = resumed?.SessionGoal ?? task.Goal;
        var startDetail = resumed?.StartDetail ?? (task.From is null ? "session container started" : "session container started (continues an earlier task's work)");
        string? startFailure = null;
        var ended = false;
        // A child of the batch's span: the task's result carries the batch's trace id, so it resolves in Langfuse.
        using var span = Telemetry.Source.StartActivity(DrivenTelemetry.TaskSpan);
        span?.SetTag("chargehand.run_id", runId);
        span?.SetTag("chargehand.task.id", task.Id);
        span?.SetTag("chargehand.task.ref", task.Ref);
        var traceId = span?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString();
        try
        {
            Directory.CreateDirectory(outDirectory);
            if (resumed is null && task.From is { } from)
            {
                // The earlier task's bundle seeds the first session, and the handoff its last session left goes after the goal.
                var (bundle, handoff) = await KeptWorkAsync(from, ct);
                seed = bundle;
                goal = handoff is null ? goal : $"{goal}\n\nThe handoff the earlier session left:\n\n{Truncated(handoff, ProgressInGoal)}";
            }
            if (resumed is null)
                journal?.Update(task.Id, e => e with
                {
                    Phase = TaskPhase.Started,
                    RunId = runId,
                    Branch = branch,
                    Started = started,
                    Container = null,
                    Continuation = 0,
                    Prior = null,
                    Runs = [runId],
                    SessionStarted = DateTimeOffset.UtcNow,
                    Best = best is null ? null : new BestBundle(best.RunId, best.Commits),
                    SessionGoal = null,
                    StartDetail = null
                });
            containers[runId] = null;
            string container;
            DateTimeOffset sessionStarted;
            if (attach)
            {
                container = resumed!.Container ?? $"chargehand-{runId}";
                sessionStarted = resumed.SessionStarted ?? resumed.Started ?? DateTimeOffset.UtcNow;
            }
            else
            {
                sessionStarted = DateTimeOffset.UtcNow;
                journal?.Update(task.Id, e => e with { SessionStarted = sessionStarted });
                container = await StartSessionAsync(task, runId, branch, goal, maxTurns, maxMinutes, Remaining(prior), prior?.UsdEstimate ?? 0, seed, sessionStarted, startDetail, span, traceId, ct);
            }
            containers[runId] = container;
            SessionOutcome session;
            string? reportJson;
            SessionOutcome total;
            while (true)
            {
                var seeded = best?.Commits ?? 0;
                var state = await WaitAsync(container, runId, task.Id, maxMinutes, progress, sessionStarted, prior, ct);
                var sessionEnded = DateTimeOffset.UtcNow;

                await volumes.FetchOutputAsync(runId, outDirectory, ct);
                journal?.Update(task.Id, e => e with { Phase = TaskPhase.Finished });
                session = ReadOutcome(outDirectory) ?? new SessionOutcome(SessionStatus.Failed, state.OomKilled ? "out_of_memory" : "no_outcome", 0, 0, 0, 0, null, state.ExitCode, [], false);
                foreach (var d in session.Governor ?? [])
                    Publish(task.Id, RunEventKind.Governor, Describe(d));
                if (session.Status == SessionStatus.NeedsInput)
                    Publish(task.Id, RunEventKind.Question, session.Questions.Count > 0 ? $"the session asked:\n{string.Join('\n', session.Questions.Select(q => $"- {q}"))}" : "the session asked for input but named no question");
                using (var call = Telemetry.Source.StartActivity(DrivenTelemetry.SessionSpan, ActivityKind.Client, span?.Context ?? default, startTime: sessionStarted))
                {
                    DrivenTelemetry.TagSession(call, session, settings.Prices ?? new Dictionary<string, ModelPrice>(), settings.UsageOnSpans);
                    call?.SetEndTime(sessionEnded.UtcDateTime);
                }
                var reportFile = Path.Combine(outDirectory, "driven-report.json");
                reportJson = File.Exists(reportFile) ? await File.ReadAllTextAsync(reportFile, ct) : null;
                total = Total(prior, session);
                progress.Report(Capped(total.InputTokens + total.OutputTokens, settings.Priced ? total.CostUsd ?? 0 : 0, ended: true));
                if (session.BundleWritten && session.Commits > 0)
                    best = new BestRun(runId, outDirectory, session.Commits);
                if (!Continues(session, continuation, total, best, seeded))
                    break;

                // The branch so far goes into a new session on the same branch name; the old container and workspace are not needed any more.
                continuation++;
                goal = ContinuationGoal(task.Goal, continuation, session, ReadProgress(outDirectory));
                startDetail = $"session container started (continuation {continuation} of {preset.MaxContinuations}, previous stopped by {session.Reason})";
                seed = Path.Combine(outDirectory, "chargehand.bundle");
                prior = total;
                await RemoveSessionAsync(runId, container, CancellationToken.None);
                var (previousRun, previousOut) = (runId, outDirectory);
                runId = Orchestrator.NewRunId();
                runs.Add(runId);
                outDirectory = Path.Combine(settings.ScratchRoot, "out", runId);
                Directory.CreateDirectory(outDirectory);
                containers[runId] = null;
                var (next, allRuns, count, earlier, seedBundle, sessionGoal, detail) = (runId, runs.ToList(), continuation, total, new BestBundle(best!.RunId, best.Commits), goal, startDetail);
                journal?.Update(task.Id, e => e with
                {
                    Phase = TaskPhase.Started,
                    RunId = next,
                    Container = null,
                    Continuation = count,
                    Prior = earlier,
                    Runs = allRuns,
                    SessionStarted = DateTimeOffset.UtcNow,
                    Best = seedBundle,
                    SessionGoal = sessionGoal,
                    StartDetail = detail
                });
                try
                {
                    sessionStarted = DateTimeOffset.UtcNow;
                    container = await StartSessionAsync(task, runId, branch, goal, maxTurns, maxMinutes, Remaining(prior), prior?.UsdEstimate ?? 0, seed, sessionStarted, startDetail, span, traceId, ct);
                }
                catch (Exception e) when (IsStartFailure(e, ct))
                {
                    // The work so far is on the previous session's bundle: carry on with it as if that session were the last (it is, now).
                    startFailure = $"continuation {continuation} could not be started: {e.Message}";
                    continuation--;
                    runs.RemoveAt(runs.Count - 1);
                    (runId, outDirectory) = (previousRun, previousOut);
                    var (back, backRuns, backCount) = (runId, runs.ToList(), continuation);
                    journal?.Update(task.Id, x => x with { Phase = TaskPhase.Finished, RunId = back, Container = null, Continuation = backCount, Runs = backRuns });
                    break;
                }
                containers[runId] = container;
            }

            var finished = session.Status == SessionStatus.Completed && reportJson is not null && DrivenReport.Parse(reportJson) is not null;
            var partial = !finished && best is not null && (Salvageable(session) || session.Status == SessionStatus.NeedsInput || best.RunId != runId);
            var handoverRun = partial ? (best!.RunId, best.OutDirectory) : (runId, outDirectory);
            HandoverOutcome? handed = null;
            if (finished)
                handed = await HandOverAsync(task, handoverRun, branch, Title(task.Goal), $"{DrivenReport.Parse(reportJson!)!.Summary}\n\nTask: {task.Id}{ContinuationNote(continuation)}", partial: false, ct);
            else if (partial)
            {
                await RestoreBundleAsync(handoverRun.Item1, ct);
                handed = await HandOverAsync(task, handoverRun, branch, $"[partial] {Title(task.Goal)}", PartialBody(task, session, total, continuation, startFailure, ReadProgress(outDirectory)), partial: true, ct);
            }

            var scratchClone = Path.Combine(settings.ScratchRoot, handoverRun.Item1);
            var children = (await log.ListAsync(new RunListQuery(Since: started, Limit: 200), ct)).Where(r => r.ParentRunId is { } parent && runs.Contains(parent)).ToList();
            var result = await DrivenResult.BuildTaskAsync(new DrivenTaskInput(task.Id, runId, traceId, settings.Model, settings.ClaudeVersion, total, reportJson, handed, children,
                Directory.Exists(scratchClone) ? scratchClone : settings.SourcePath, settings.BaseCommit, settings.Priced,
                partial ? new KeptWork(branch, best!.RunId, best.Commits, settings.BaseCommit) : null), resolver, supportCheck, ct);
            if (sign is not null)
                result = sign(result);
            await log.AppendAsync(new StartRecord(runId, started, traceId, settings.Request with { Text = task.Goal, Driven = null }, Environment.ProcessId, settings.BatchId, ProcessBoot.Id), CancellationToken.None);
            await log.AppendAsync(new RunRecord(runId, started, DateTimeOffset.UtcNow, settings.Request.Context.Preset, null, null, result), CancellationToken.None);
            var startNote = startFailure is null ? null : $"; continuation {continuation + 1} did not start";
            (string? Detail, SessionOutcome? Spent) follow = (null, null);
            if (FollowsUp(handed))
            {
                // A restart during the follow-up keeps this outcome rather than handing the task over again; the pull request itself holds the count of fix attempts (ADR 0046).
                journal?.Finish(Outcome(task, runId, result, total, handed, settings.Priced, partial, startNote));
                follow = await FollowUpAsync(task, handed!, Path.Combine(handoverRun.Item2, "chargehand.bundle"), best?.Commits ?? 0, branch, partial, total, containers, span, traceId, progress, ct);
            }
            var spent = follow.Spent ?? total;
            var outcome = Outcome(task, runId, result, spent, handed, settings.Priced, partial, startNote);
            if (follow.Detail is { } integration)
                outcome = outcome with { Detail = outcome.Detail is { } before ? $"{before}; {integration}" : integration };
            journal?.Finish(outcome);
            ended = true;
            span?.SetTag("chargehand.task.continuations", continuation);
            Tag(span, outcome, spent, started);
            return outcome;
        }
        catch (Exception e)
        {
            span?.SetTag("chargehand.task.state", e is OperationCanceledException ? "cancelled" : "failed");
            span?.SetTag("chargehand.error.code", e is OperationCanceledException ? "cancelled" : JsonNamingPolicy.SnakeCaseLower.ConvertName(ChargehandException.ErrorOf(e).Code.ToString()));
            span?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            // A server that is stopping leaves an unfinished task as it is: the next process takes it up (ADR 0043).
            if (ended || !Detaching)
            {
                if (task.From is { } continued)
                    foreach (var earlierRun in new[] { continued.LastRun, continued.Kept?.BundleRun }.OfType<string>())
                        DeleteQuietly(Path.Combine(settings.ScratchRoot, "out", earlierRun));
                // A start cancelled in flight can leave the container docker already made; the name is fixed, so remove it by name.
                containers.TryAdd(runId, null);
                foreach (var (run, name) in containers)
                {
                    await RemoveSessionAsync(run, name, CancellationToken.None);
                    DeleteQuietly(Path.Combine(settings.ScratchRoot, "out", run));
                    DeleteQuietly(Path.Combine(settings.ScratchRoot, run));
                    // The last session's output volume and the one the handover read stay, as they always have; an earlier session's is no use once a later one has its bundle.
                    if (run != runId && run != best?.RunId)
                        await engine.RemoveVolumeAsync(RunnerNames.Out(run), CancellationToken.None);
                }
            }
        }
    }

    /// <summary>The task's pull request is on a repository the profile opted in to integration (ADR 0046).</summary>
    private bool FollowsUp(HandoverOutcome? handed) =>
        settings.Integration is { } integrator && handed?.PullRequest is not null && RemoteRepository.Parse(settings.RemoteUrl) is { } repository && integrator.Mode(repository) != IntegrationMode.Off;

    /// <summary>After the handover, on an opted-in repository: mark the draft ready and merge it by policy, then watch its required checks and, when some fail, run short fix sessions on the same branch
    /// seeded from the last bundle (<see cref="CiFixSession"/>). The pull request exists whatever happens here, so a GitHub error or a cancel ends in a line of detail, never in a failed task.</summary>
    /// <returns>The line for the task outcome, and the usage of the fix sessions added to the task's (null: none ran).</returns>
    private async Task<(string? Detail, SessionOutcome? Spent)> FollowUpAsync(ResolvedTask task, HandoverOutcome handed, string seedBundle, int seededCommits, string branch, bool partial, SessionOutcome total,
        Dictionary<string, string?> containers, Activity? span, string traceId, IProgress<TaskUsage> progress, CancellationToken ct)
    {
        var integrator = settings.Integration!;
        var pr = handed.PullRequest!;
        var target = new IntegrationTarget(RemoteRepository.Parse(settings.RemoteUrl)!, pr.Number, branch, partial, task.Goal, handed.TestIntegrity?.Reason);
        void Report(string detail) => Publish(task.Id, RunEventKind.Integrated, detail, s => s with { PrUrl = pr.Url, Branch = branch });
        SessionOutcome? fixSum = null;
        var fixes = 0;
        var seed = seedBundle;

        async Task<CiFixResult> Fix(CiFixRequest request, CancellationToken token)
        {
            var running = fixSum is null ? total : Total(fixSum, total);
            var (tokensLeft, usdLeft) = Remaining(running);
            if (tokensLeft <= 0 || usdLeft is <= 0)
                return new CiFixResult(false, "the task's token or cost cap is spent");
            if (!File.Exists(seed))
                return new CiFixResult(false, "the branch's bundle is gone, so a session cannot start from it");
            var fixRun = Orchestrator.NewRunId();
            containers[fixRun] = null;
            var outDirectory = Path.Combine(settings.ScratchRoot, "out", fixRun);
            Directory.CreateDirectory(outDirectory);
            var sessionStarted = DateTimeOffset.UtcNow;
            var container = await StartSessionAsync(task, fixRun, branch, request.Goal, integrator.CiFix.Turns, integrator.CiFix.Minutes, (tokensLeft, usdLeft), running.UsdEstimate ?? 0, seed, sessionStarted,
                $"session container started (CI fix attempt {request.Attempt} of {request.MaxAttempts})", span, traceId, token);
            containers[fixRun] = container;
            await WaitAsync(container, fixRun, task.Id, integrator.CiFix.Minutes, progress, sessionStarted, running, token);
            await volumes.FetchOutputAsync(fixRun, outDirectory, token);
            var session = ReadOutcome(outDirectory) ?? new SessionOutcome(SessionStatus.Failed, "no_outcome", 0, 0, 0, 0, null, null, [], false);
            fixSum = fixSum is null ? session : Total(fixSum, session);
            progress.Report(Capped(fixSum.InputTokens + fixSum.OutputTokens + total.InputTokens + total.OutputTokens, settings.Priced ? (fixSum.CostUsd ?? 0) + (total.CostUsd ?? 0) : 0, ended: true));
            if (session is not { Status: SessionStatus.Completed, BundleWritten: true } || session.Commits <= seededCommits)
                return new CiFixResult(false, session.Status == SessionStatus.Completed ? "the session made no new commit" : $"the session ended {StopReason(session)}");
            var fixHandover = await handover.RunAsync(new HandoverInput(fixRun, outDirectory, settings.SourcePath, settings.BaseCommit, branch, settings.RemoteUrl, settings.BaseBranch, Title(task.Goal),
                $"CI fix attempt {request.Attempt}", settings.ScratchRoot, settings.Push, settings.Request.Context.Verify, [.. settings.ModelEnvironment.Values], AllowLocalRemote: settings.AllowLocalRemote,
                Report: e => { if (e.Kind != RunEventKind.PrOpened) Publish(task.Id, e.Kind, e.Detail, s => s with { Branch = e.Kind == RunEventKind.Pushed ? branch : null }); }), token);
            if (fixHandover.Status == HandoverStatus.NotPushed)
                return new CiFixResult(false, $"not pushed: {fixHandover.Reason}");
            (seed, seededCommits) = (Path.Combine(outDirectory, "chargehand.bundle"), session.Commits);
            fixes++;
            return new CiFixResult(true, "chargehand's own tests passed and the commits are on the branch");
        }

        try
        {
            var first = await integrator.IntegrateAsync(target, Report, ct);
            var detail = $"integration: {first.Detail}";
            if (first.Action is IntegrationAction.Asked or IntegrationAction.AutoMergeEnabled)
            {
                var last = await integrator.FollowAsync(target, Fix, Report, ct);
                detail += $"; {last.Detail}{(fixes > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({fixes} CI fix attempt(s) pushed)") : "")}";
            }
            return (detail, fixSum is null ? null : Total(fixSum, total));
        }
        catch (Exception e) when (IsStartFailure(e, ct) || e is OperationCanceledException && ct.IsCancellationRequested)
        {
            Report($"integration ended early: {(e is OperationCanceledException ? "cancelled" : e.Message)}");
            return ($"integration ended early: {(e is OperationCanceledException ? "cancelled" : e.Message)}", fixSum is null ? null : Total(fixSum, total));
        }
    }

    /// <summary>A continuation's start that failed for a reason outside the task (the runner is full or unreachable, the seed was refused, the call timed out) must not cost the work
    /// the earlier sessions left. A cancel by the caller or a stopping server is not one.</summary>
    private static bool IsStartFailure(Exception e, CancellationToken ct) =>
        e is ChargehandException or HttpRequestException or IOException || (e is TaskCanceledException && !ct.IsCancellationRequested);

    /// <summary>The bundle of an earlier session on the host: the file the task kept, or else read again from that session's output volume (a restart may have cleared the scratch directory).</summary>
    private async Task<string?> RestoreBundleAsync(string run, CancellationToken ct)
    {
        var directory = Path.Combine(settings.ScratchRoot, "out", run);
        var bundle = Path.Combine(directory, "chargehand.bundle");
        if (File.Exists(bundle))
            return bundle;
        Directory.CreateDirectory(directory);
        try
        {
            await volumes.FetchOutputAsync(run, directory, ct);
        }
        catch (ChargehandException)
        {
            return null;
        }
        return File.Exists(bundle) ? bundle : null;
    }

    /// <summary>What a task that continues an earlier one starts from: the bundle the earlier task kept (null: it made no commits) and the handoff its last session left (null: none).</summary>
    /// <exception cref="ChargehandException">The earlier task kept a bundle and its output volume no longer holds it.</exception>
    private async Task<(string? Bundle, string? Handoff)> KeptWorkAsync(ContinuedFrom from, CancellationToken ct)
    {
        var bundle = from.Kept is null ? null
            : await RestoreBundleAsync(from.Kept.BundleRun, ct) ?? throw new ChargehandException(ErrorCode.InvalidRequest,
                $"the work of the task this one continues is gone: its output volume is kept for {ContinuationSource.KeptFor.TotalDays:0} days after it ended",
                "Send the task again with a goal of its own.");
        await RestoreBundleAsync(from.LastRun, ct);
        return (bundle, ReadProgress(Path.Combine(settings.ScratchRoot, "out", from.LastRun)));
    }

    private static string StopReason(SessionOutcome session) =>
        session.Reason.Length > 0 ? session.Reason : JsonNamingPolicy.SnakeCaseLower.ConvertName(session.Status.ToString());

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

    private static string Truncated(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "\n\n[handoff truncated]";

    private static string PartialBody(ResolvedTask task, SessionOutcome last, SessionOutcome total, int continuation, string? startFailure, string? progress)
    {
        var body = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"Partial result: the session {(last.Status == SessionStatus.NeedsInput ? "stopped to ask a question" : $"was stopped by {StopReason(last).Replace('_', ' ')}")} after {total.Turns} turns and did not finish. This is unfinished work, kept so it is not lost; read it before anything else.\n\nTask: {task.Id}"))
            .Append(ContinuationNote(continuation));
        if (last.Status == SessionStatus.NeedsInput && last.Questions.Count > 0)
            body.Append("\n\nQuestions for you:\n").AppendJoin('\n', last.Questions.Select(q => $"- {q}"));
        if (startFailure is not null)
            body.Append("\n\nA further session could not be started: ").Append(startFailure).Append('.');
        if (last.SnapshotNote is { Length: > 0 } note)
            body.Append("\n\nNot everything the session wrote is on this branch: ").Append(note).Append('.');
        if (progress is not null)
            body.Append("\n\n---\n\n").Append(Truncated(progress, ProgressInBody));
        return body.ToString();
    }

    /// <summary>The task's usage as the scheduler sees it. The session's driver stops at the cap, so the tally it leaves, and the final sum, are a little over: once the session has
    /// <paramref name="ended"/>, or while it is within a tenth of the cap, the report is held at the cap, because a report past it has the scheduler cancel the task during the wind-down
    /// and the handover with it. A runaway tally further over is reported as it is, and the scheduler stops the task.</summary>
    private TaskUsage Capped(long tokens, decimal usd, bool ended)
    {
        var (maxTokens, maxUsd) = (settings.Limits.MaxTokens, settings.Limits.MaxUsd);
        return new TaskUsage(ended || tokens <= maxTokens + maxTokens / 10 ? Math.Min(tokens, maxTokens) : tokens,
            maxUsd is { } cap && (ended || usd <= cap + cap / 10) ? Math.Min(usd, cap) : usd);
    }

    /// <summary>The session's container, workspace and task file, up to the container's start.</summary>
    private async Task<string> StartSessionAsync(ResolvedTask task, string runId, string branch, string goal, int maxTurns, int maxMinutes, (long Tokens, decimal? Usd) left, decimal spentUsd,
        string? seed, DateTimeOffset sessionStarted, string detail, Activity? span, string traceId, CancellationToken ct)
    {
        var preset = settings.Preset;
        await engine.CreateVolumeAsync(RunnerNames.Out(runId), runId, ct);
        await workspace.PrepareWorkspaceAsync(new WorkspaceSpec(runId, settings.Image, settings.SourcePath, RunnerNames.Work(runId), branch, settings.BaseCommit), ct);
        if (seed is not null)
            await volumes.WriteSeedAsync(runId, seed, ct);
        await volumes.WriteTaskAsync(runId, new SessionTask(runId, goal, branch, maxTurns, maxMinutes, preset.NoProgressMinutes * 60, left.Tokens, MaxUsd: left.Usd, Prices: settings.Prices,
            Governor: settings.Governor?.ToSession(task.Size, task.BudgetUsd, spentUsd), Environment: settings.Shared?.ForSession()), ct);

        var gateway = settings.GatewayKey is not null && settings.Network.ModelUrl is not null;
        Dictionary<string, string> env = new(gateway ? new Dictionary<string, string>() : settings.ModelEnvironment, StringComparer.Ordinal)
        {
            ["HTTPS_PROXY"] = settings.Network.ProxyUrl(runId),
            ["HTTP_PROXY"] = settings.Network.ProxyUrl(runId),
            ["CHARGEHAND_RUN_ID"] = runId,
        };
        if (settings.ModelBaseUrl is { } modelBaseUrl)
            env["ANTHROPIC_BASE_URL"] = modelBaseUrl;
        if (gateway)
        {
            // The container holds a token that is worth nothing outside this run and this network; the real credential stays in the egress container.
            env["ANTHROPIC_BASE_URL"] = settings.Network.ModelUrl!;
            env["CLAUDE_CODE_OAUTH_TOKEN"] = ModelTokens.Mint(settings.GatewayKey!, runId, sessionStarted.AddMinutes(maxMinutes + 15), left.Tokens);
            env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        }
        if (settings.CallbackForwards || gateway)
            // A forward and the model endpoint are plain HTTP to the egress container; the proxy variables would send them to that same proxy, which answers CONNECT only.
            env["NO_PROXY"] = env["no_proxy"] = $"{settings.Network.ProxyHost},{ContainerTemplate.CallbackAlias}";
        if (settings.Shared is { } shared)
        {
            foreach (var (name, value) in shared.Environment())
                env[name] = value;
            shared.BypassProxy(env);
        }
        if (settings.Otlp is { } otlp)
            foreach (var (name, value) in otlp.Environment(settings.BatchId, task.Id, runId, span is null ? null : traceId))
                env[name] = value;
        if (settings.McpUrl is { } mcp)
        {
            env["CHARGEHAND_MCP_URL"] = mcp;
            // The token admits only this path and commit; /work is not it, so the session is told them.
            env["CHARGEHAND_REPOSITORY_PATH"] = settings.SourcePath;
            env["CHARGEHAND_BASE_COMMIT"] = settings.BaseCommit;
            env["CHARGEHAND_RUN_TOKEN"] = mint(new TaskGrant(runId, settings.SourcePath, settings.BaseCommit, left.Usd, left.Tokens, sessionStarted.AddMinutes(maxMinutes + 15)));
        }
        var container = await engine.StartAsync(new ContainerSpec(runId, settings.Image, RunnerNames.Work(runId), RunnerNames.Out(runId), settings.Network.Network, env,
            preset.MemoryMb, preset.Cpus, preset.Pids, ["session"], Caches: settings.Shared?.Caches is { Count: > 0 } caches ? caches : null), ct);
        journal?.Update(task.Id, e => e with { Container = container });
        Publish(task.Id, RunEventKind.ContainerStarted, detail, s => s with { Branch = branch });
        return container;
    }

    /// <summary>What is left of the task's caps once the earlier sessions have spent theirs (ADR 0044): the caps are the task's, not each session's.</summary>
    private (long Tokens, decimal? Usd) Remaining(SessionOutcome? prior) =>
        (settings.Limits.MaxTokens - (prior is null ? 0 : prior.InputTokens + prior.OutputTokens),
            settings.Limits.MaxUsd is { } cap ? cap - (prior?.CostUsd ?? 0) : null);

    /// <summary>A session stopped by the turn limit or the wall clock (<see cref="StallDetector.ContinuableReasons"/>) with work on its branch gets another, while continuations and the task's caps
    /// last and the session added a commit past the one it was seeded with. Not a stall for no progress or a repeated call (it would loop again), not the token cap, not a question for a person.</summary>
    private bool Continues(SessionOutcome session, int continuation, SessionOutcome total, BestRun? best, int seeded)
    {
        if (session is not { Status: SessionStatus.Stalled, BundleWritten: true, Commits: > 0 } || !StallDetector.Continuable(session.Reason) || best is null
            || continuation >= settings.Preset.MaxContinuations || session.Commits <= seeded)
            return false;
        var (tokens, usd) = Remaining(total);
        return tokens > 0 && usd is not <= 0;
    }

    /// <summary>A session that did not finish but left commits is handed over as a partial draft pull request, a rate-limited one too (ADR 0045: its work is not lost while the
    /// limit lasts); a question for a person, or a session that ended without its work, is not.</summary>
    private static bool Salvageable(SessionOutcome session) =>
        session.Status is SessionStatus.Stalled or SessionStatus.TokenCap or SessionStatus.RateLimited || (session.Status == SessionStatus.Failed && session.Reason != "out_of_memory");

    /// <summary>A governor decision as a run event's detail: what it would do, the signal and the rule, the turn, and the spend against the budget when known.</summary>
    internal static string Describe(GovernorDecision d)
    {
        var spend = d.SpendUsd is { } usd && d.BudgetUsd is { } budget ? string.Create(CultureInfo.InvariantCulture, $", spend {usd:0.00} of {budget:0.00}") : "";
        var mode = d.Acted ? "" : $"{JsonNamingPolicy.SnakeCaseLower.ConvertName(d.Mode.ToString())}: ";
        return string.Create(CultureInfo.InvariantCulture, $"governor {mode}{JsonNamingPolicy.SnakeCaseLower.ConvertName(d.Action.ToString())} on {d.Signal} ({d.Rule.Replace('_', ' ')}) at turn {d.Turn}{spend}");
    }

    /// <summary>With a handoff (ADR 0045) the earlier commits are offered, not required: the handoff may show the approach failed, and then the session starts again from the base commit.</summary>
    private string ContinuationGoal(string goal, int number, SessionOutcome previous, string? progress)
    {
        var head = $"{goal}\n\nContinuation {number} of {settings.Preset.MaxContinuations}. A previous session was stopped by {previous.Reason.Replace('_', ' ')} after {previous.Turns} turns; its work is on this branch "
            + "(the last commit, 'WIP: ...'). ";
        return progress is null
            ? head + $"Read `git log` and `git diff {settings.BaseCommit}..HEAD` first, do not redo finished work, finish the task, run the tests, and end with the same report block."
            : head + $"The handoff below says where it stood. Read it, then `git log` and `git diff {settings.BaseCommit}..HEAD`. Those commits are offered, not required: keep what works and do not redo it, "
                + $"but if the handoff shows the approach failed, run `git reset --hard {settings.BaseCommit}` and take another approach. Finish the task, run the tests, and end with the same report block.\n\n"
                + Truncated(progress, ProgressInGoal);
    }

    private static string ContinuationNote(int continuation) =>
        continuation == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"\n\nThe task ran {continuation} continuation session(s) on this branch after earlier ones were stopped.");

    private Task<HandoverOutcome> HandOverAsync(ResolvedTask task, (string RunId, string OutDirectory) run, string branch, string title, string body, bool partial, CancellationToken ct) =>
        handover.RunAsync(new HandoverInput(run.RunId, run.OutDirectory, settings.SourcePath, settings.BaseCommit, branch, settings.RemoteUrl, settings.BaseBranch,
            title, body, settings.ScratchRoot, settings.Push, settings.Request.Context.Verify, [.. settings.ModelEnvironment.Values], AllowLocalRemote: settings.AllowLocalRemote,
            Report: e => Publish(task.Id, e.Kind, e.Detail, s => s with { PrUrl = e.PrUrl, Branch = e.Kind == RunEventKind.Pushed ? branch : null }), Partial: partial,
            Goal: task.Goal, PolicyNote: RemoteRepository.Parse(settings.RemoteUrl) is { } repository ? settings.Integration?.Describe(repository, partial) : null), ct);

    private static SessionOutcome Total(SessionOutcome? earlier, SessionOutcome last)
    {
        if (earlier is null)
            return last;
        Dictionary<string, TokenCounts> models = new(earlier.ModelUsage ?? new Dictionary<string, TokenCounts>());
        foreach (var (name, now) in last.ModelUsage ?? new Dictionary<string, TokenCounts>())
            models[name] = models.TryGetValue(name, out var before)
                ? new TokenCounts(before.Input + now.Input, before.Output + now.Output, before.Reasoning + now.Reasoning, before.CacheRead + now.CacheRead, before.CacheWrite + now.CacheWrite)
                : now;
        return last with
        {
            Turns = earlier.Turns + last.Turns,
            InputTokens = earlier.InputTokens + last.InputTokens,
            OutputTokens = earlier.OutputTokens + last.OutputTokens,
            CacheReadTokens = earlier.CacheReadTokens + last.CacheReadTokens,
            CacheWriteTokens = earlier.CacheWriteTokens + last.CacheWriteTokens,
            CostUsd = earlier.CostUsd is null && last.CostUsd is null ? null : (earlier.CostUsd ?? 0) + (last.CostUsd ?? 0),
            UsdEstimate = earlier.UsdEstimate is null && last.UsdEstimate is null ? null : (earlier.UsdEstimate ?? 0) + (last.UsdEstimate ?? 0),
            UsdEstimatePartial = earlier.UsdEstimatePartial || last.UsdEstimatePartial,
            ModelUsage = models.Count == 0 ? null : models,
        };
    }

    private async Task RemoveSessionAsync(string runId, string? container, CancellationToken ct)
    {
        await engine.RemoveAsync(container ?? $"chargehand-{runId}", ct);
        await engine.RemoveVolumeAsync(RunnerNames.Work(runId), ct);
    }

    private bool Detaching => detaching?.Invoke() == true;

    /// <summary>A task begun before a restart whose container is gone and whose output volume holds no outcome: there is nothing to take up.</summary>
    private async Task<bool> GoneWithoutOutputAsync(TaskEntry entry, CancellationToken ct)
    {
        if ((await engine.InspectAsync(entry.Container ?? $"chargehand-{entry.RunId}", ct)).Status != ContainerStatus.Missing)
            return false;
        var directory = Path.Combine(settings.ScratchRoot, "out", entry.RunId!);
        Directory.CreateDirectory(directory);
        try
        {
            await volumes.FetchOutputAsync(entry.RunId!, directory, ct);
        }
        catch (ChargehandException)
        {
            return true;
        }
        return !File.Exists(Path.Combine(directory, "session-outcome.json"));
    }

    private async Task RemoveRunAsync(string runId, string? container)
    {
        await engine.RemoveAsync(container ?? $"chargehand-{runId}", CancellationToken.None);
        await engine.RemoveVolumeAsync(RunnerNames.Work(runId), CancellationToken.None);
        await engine.RemoveVolumeAsync(RunnerNames.Out(runId), CancellationToken.None);
        DeleteQuietly(Path.Combine(settings.ScratchRoot, "out", runId));
        DeleteQuietly(Path.Combine(settings.ScratchRoot, runId));
    }

    /// <summary>Waits for the container to end. A cancel interrupts the session, gives it a moment, then kills it, and rethrows; a session past its wall clock plus a margin is killed.</summary>
    private async Task<ContainerState> WaitAsync(string container, string runId, string taskId, int maxMinutes, IProgress<TaskUsage> progress, DateTimeOffset since, SessionOutcome? prior, CancellationToken ct)
    {
        var every = poll ?? DefaultPoll;
        var usageClock = Stopwatch.StartNew();
        long reported = 0;
        TaskUsage? shown = null;
        // A continuation's tally starts at zero; the task's numbers are the earlier sessions' plus it.
        var earlierTokens = prior is null ? 0 : prior.InputTokens + prior.OutputTokens;
        var earlierUsd = settings.Priced ? prior?.CostUsd ?? 0 : 0;
        var earlierTurns = prior?.Turns ?? 0;
        try
        {
            while (true)
            {
                var state = await engine.InspectAsync(container, ct);
                if (state.Status != ContainerStatus.Running)
                    return state;
                if (DateTimeOffset.UtcNow - since > TimeSpan.FromMinutes(maxMinutes) + WallClockSlack)
                {
                    await engine.SignalAsync(container, "SIGKILL", CancellationToken.None);
                    return await engine.InspectAsync(container, CancellationToken.None);
                }
                if (usageClock.Elapsed >= (poll ?? DefaultUsagePoll))
                {
                    usageClock.Restart();
                    var usage = await ReadUsageQuietly(runId, ct);
                    if (usage is not null && usage != shown)
                    {
                        shown = usage;
                        {
                            var whole = usage with
                            {
                                Tokens = earlierTokens + usage.Tokens,
                                Usd = earlierUsd,
                                Turns = usage.Turns is { } turns ? earlierTurns + turns : null,
                                UsdEstimate = usage.UsdEstimate is { } estimate ? estimate + (prior?.UsdEstimate ?? 0) : null,
                                CacheRead = usage.CacheRead is { } read ? read + (prior?.CacheReadTokens ?? 0) : null,
                                CacheWrite = usage.CacheWrite is { } write ? write + (prior?.CacheWriteTokens ?? 0) : null,
                                UsdEstimatePartial = usage.UsdEstimatePartial || prior is { UsdEstimatePartial: true }
                            };
                            Publish(taskId, RunEventKind.SessionProgress, Describe(whole), s => s with { Tokens = whole.Tokens, Turns = whole.Turns, Stage = whole.Stage, UsdEstimate = whole.UsdEstimate, CacheRead = whole.CacheRead, CacheWrite = whole.CacheWrite, UsdEstimatePartial = whole.UsdEstimatePartial ? true : null });
                        }
                    }
                    if (usage is not null && usage.Tokens > reported)
                    {
                        reported = usage.Tokens;
                        progress.Report(Capped(earlierTokens + usage.Tokens, earlierUsd, ended: false));
                    }
                }
                await Task.Delay(every, ct);
            }
        }
        catch (OperationCanceledException) when (!Detaching)
        {
            await engine.SignalAsync(container, "SIGINT", CancellationToken.None);
            var grace = Stopwatch.StartNew();
            while (grace.Elapsed < (stopGrace ?? DefaultGrace) && (await engine.InspectAsync(container, CancellationToken.None)).Status == ContainerStatus.Running)
                await Task.Delay(every, CancellationToken.None);
            await engine.SignalAsync(container, "SIGKILL", CancellationToken.None);
            throw;
        }
    }

    private void Publish(string taskId, RunEventKind kind, string detail, Func<RunStatus, RunStatus>? with = null)
    {
        if (publish is null)
            return;
        var e = RunStatus.Of(settings.BatchId, RunState.Running, kind) with { TaskId = taskId, Detail = $"task {taskId}: {detail}" };
        publish(with is null ? e : with(e));
    }

    private static string Describe(TaskUsage usage) =>
        string.Join(", ", new[]
        {
            string.Create(CultureInfo.InvariantCulture, $"{usage.Tokens} tokens"),
            usage.Turns is { } turns ? string.Create(CultureInfo.InvariantCulture, $"{turns} turns") : null,
            usage.Stage is { } stage ? $"stage {stage.ToString().ToLowerInvariant()}" : null,
            usage.UsdEstimate is { } estimate ? string.Create(CultureInfo.InvariantCulture, $"about ${estimate:0.00} estimated{(usage.UsdEstimatePartial ? " (partial)" : "")}") : null,
        }.Where(p => p is not null));

    /// <summary>A poll that fails (the engine busy, a half-written file) is skipped: the final outcome still carries the real numbers.</summary>
    private async Task<TaskUsage?> ReadUsageQuietly(string runId, CancellationToken ct)
    {
        try
        {
            return await volumes.ReadUsageAsync(runId, ct);
        }
        catch (Exception e) when (e is ChargehandException or IOException or JsonException)
        {
            return null;
        }
    }

    private static SessionOutcome? ReadOutcome(string directory)
    {
        var file = Path.Combine(directory, "session-outcome.json");
        if (!File.Exists(file))
            return null;
        try
        {
            return JsonSerializer.Deserialize<SessionOutcome>(File.ReadAllText(file), OutcomeJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TaskOutcome Outcome(ResolvedTask task, string runId, ResultContract result, SessionOutcome session, HandoverOutcome? handed, bool priced, bool partial, string? partialNote)
    {
        var state = result.Status switch
        {
            ResultStatus.Completed => TaskState.Completed,
            ResultStatus.NeedsInput => TaskState.NeedsInput,
            _ => TaskState.Failed,
        };
        var pushed = handed?.Status is HandoverStatus.Pushed or HandoverStatus.PrFailed;
        var detail = state == TaskState.Completed ? handed?.TestIntegrity is { } flag ? $"test integrity: {flag.Reason}" : null
            : !partial || handed is null ? result.Summary
            : session.Status == SessionStatus.NeedsInput ? $"partial: needs input ({string.Join("; ", session.Questions)}); {(pushed ? handed.PullRequest is null ? "branch pushed, pull request failed" : "draft PR opened" : $"not handed over: {handed.Reason}")}{partialNote}"
            : pushed ? $"partial: stopped by {StopReason(session)}; {(handed.PullRequest is null ? "branch pushed, pull request failed" : "draft PR opened")}{partialNote}"
            : $"partial: stopped by {StopReason(session)}; not handed over: {handed.Reason}{partialNote}";
        return new TaskOutcome(task.Id, state, runId, detail, result.Error?.Code,
            priced ? session.CostUsd ?? 0 : 0, session.InputTokens + session.OutputTokens, pushed ? handed!.Branch : null, handed?.PullRequest?.Url,
            session.Status == SessionStatus.RateLimited ? session.RateLimitResetsAt : null);
    }

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

    private static string Title(string goal)
    {
        var line = goal.Split('\n', 2)[0].Trim();
        return line.Length > 72 ? line[..72].TrimEnd() : line;
    }

    private static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch directory is swept with the worker root; it must not turn a finished task into a failure.
        }
    }
}
