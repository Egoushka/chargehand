using System.Diagnostics;
using System.Globalization;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Gateway.Api;
using Chargehand.Modules.Journal.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Modules.Tasks.Api;

/// <summary>The task's sessions: the first one started or taken up, each one's end read and recorded, and the continuations (ADR 0044).</summary>
public sealed partial class TaskRunner
{
    private async Task FirstSessionAsync(ResolvedTask task, TaskRun run, CancellationToken ct)
    {
        run.Containers[run.RunId] = null;
        if (run.Attach)
        {
            run.Container = run.Resumed!.Container ?? $"chargehand-{run.RunId}";
            run.SessionStarted = run.Resumed.SessionStarted ?? run.Resumed.Started ?? DateTimeOffset.UtcNow;
        }
        else
        {
            run.SessionStarted = DateTimeOffset.UtcNow;
            var sessionStarted = run.SessionStarted;
            state?.Update(task.Id, e => e with { SessionStarted = sessionStarted });
            run.Open = (run.RunId, run.SessionStarted, run.Continuation == 0 ? "first" : "continuation");
            run.Container = await StartSessionAsync(task, run.RunId, run.Branch, run.Goal, run.MaxTurns, run.MaxMinutes, Remaining(run.Prior), run.Prior?.UsdEstimate ?? 0, run.Seed, run.SessionStarted,
                run.StartDetail, run.Span, run.TraceId, run.Budget, ct);
        }
        run.Open ??= (run.RunId, run.SessionStarted, run.Continuation == 0 ? "first" : "continuation");
        run.Containers[run.RunId] = run.Container;
    }

    /// <summary>Waits for the session to end, reads and records its outcome, and adds its usage to the task's.</summary>
    /// <returns>True when the session gets a continuation.</returns>
    private async Task<bool> EndSessionAsync(ResolvedTask task, TaskRun run, IProgress<TaskUsage> progress, CancellationToken ct)
    {
        var (runId, outDirectory) = (run.RunId, run.OutDirectory);
        var seeded = run.Best?.Commits ?? 0;
        ContainerState exited;
        var live = run.Container;
        state?.SessionStarted(task.Id, () => engine.SignalAsync(live, "SIGKILL", CancellationToken.None));
        try
        {
            exited = await WaitAsync(run.Container, runId, task.Id, run.MaxMinutes, progress, run.SessionStarted, run.Prior, ct);
        }
        finally
        {
            state?.SessionEnded(task.Id);
        }
        var sessionEnded = DateTimeOffset.UtcNow;

        await Tolerant(task.Id, () => volumes.FetchOutputAsync(runId, outDirectory, ct), ct);
        run.Session = SessionDriver.ReadOutcome(outDirectory) ?? new SessionOutcome(SessionStatus.Failed, exited.OomKilled ? "out_of_memory" : "no_outcome", 0, 0, 0, 0, null, exited.ExitCode, [], false);
        NoteSession(task, run, sessionEnded);
        // Saved after the session's events are published: a task that is Finished has published them, and a restart that reads the outcome again does not.
        state?.Update(task.Id, e => e with { Phase = TaskPhase.Finished });
        var session = run.Session;
        var reportFile = Path.Combine(outDirectory, "driven-report.json");
        run.ReportJson = File.Exists(reportFile) ? await File.ReadAllTextAsync(reportFile, ct) : null;
        run.Total = Total(run.Prior, session);
        progress.Report(Capped(run.Total.InputTokens + run.Total.OutputTokens, settings.Priced ? run.Total.CostUsd ?? 0 : 0, ended: true));
        if (session.BundleWritten && session.Commits > 0)
            run.Best = new BestRun(runId, outDirectory, session.Commits);
        return Continues(session, run.Continuation, run.Total, run.Best, seeded);
    }

    /// <summary>The session's governor decisions, records, question and span. An outcome read again after a restart (the task was saved Finished) publishes no events: they were published before.</summary>
    private void NoteSession(ResolvedTask task, TaskRun run, DateTimeOffset sessionEnded)
    {
        var session = run.Session;
        var reread = run.Resumed is { Phase: TaskPhase.Finished } resumed && resumed.RunId == run.RunId;
        run.Decisions.AddRange(session.Governor ?? []);
        foreach (var d in reread ? [] : session.Governor ?? [])
            Publish(task.Id, RunEventKind.Governor, Describe(d));
        RecordSession(task, run.Open!.Value.Role, run.RunId, run.SessionStarted, sessionEnded, session);
        RecordGovernor(task, run.RunId, run.OutDirectory, session);
        run.Open = null;
        if (session.Status == SessionStatus.NeedsInput && !reread)
            Publish(task.Id, RunEventKind.Question, session.Questions.Count > 0 ? $"the session asked:\n{string.Join('\n', session.Questions.Select(q => $"- {q}"))}" : "the session asked for input but named no question");
        using var call = Telemetry.Source.StartActivity(DrivenTelemetry.SessionSpan, ActivityKind.Client, run.Span?.Context ?? default, startTime: run.SessionStarted);
        DrivenTelemetry.TagSession(call, session, settings.Prices ?? new Dictionary<string, ModelPrice>(), settings.UsageOnSpans);
        call?.SetEndTime(sessionEnded.UtcDateTime);
    }

    /// <summary>The branch so far goes into a new session on the same branch name; the old container and workspace are not needed any more.</summary>
    /// <returns>False when the continuation could not be started: the previous session is then the last.</returns>
    private async Task<bool> ContinueAsync(ResolvedTask task, TaskRun run, CancellationToken ct)
    {
        var session = run.Session;
        run.Continuation++;
        run.Goal = ContinuationGoal(task.Goal, run.Continuation, session, ReadProgress(run.OutDirectory));
        run.StartDetail = $"session container started (continuation {run.Continuation} of {settings.Preset.MaxContinuations}, previous stopped by {session.Reason})";
        run.Seed = Path.Combine(run.OutDirectory, "chargehand.bundle");
        run.Prior = run.Total;
        await RemoveSessionAsync(run.RunId, run.Container, CancellationToken.None);
        var (previousRun, previousOut) = (run.RunId, run.OutDirectory);
        run.RunId = RunIds.New();
        run.Runs.Add(run.RunId);
        run.OutDirectory = Path.Combine(settings.ScratchRoot, "out", run.RunId);
        Directory.CreateDirectory(run.OutDirectory);
        run.Containers[run.RunId] = null;
        var (next, allRuns, count, earlier, seedBundle, sessionGoal, detail) = (run.RunId, run.Runs.ToList(), run.Continuation, run.Total, new BestBundle(run.Best!.RunId, run.Best.Commits), run.Goal, run.StartDetail);
        state?.Update(task.Id, e => e with
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
            run.SessionStarted = DateTimeOffset.UtcNow;
            run.Open = (run.RunId, run.SessionStarted, "continuation");
            run.Container = await StartSessionAsync(task, run.RunId, run.Branch, run.Goal, run.MaxTurns, run.MaxMinutes, Remaining(run.Prior), run.Prior?.UsdEstimate ?? 0, run.Seed, run.SessionStarted,
                run.StartDetail, run.Span, run.TraceId, run.Budget, ct);
        }
        catch (Exception e) when (IsStartFailure(e, ct))
        {
            // The work so far is on the previous session's bundle: carry on with it as if that session were the last (it is, now).
            run.StartFailure = $"continuation {run.Continuation} could not be started: {e.Message}";
            RecordCutShort(task, run.Open!.Value, e);
            run.Open = null;
            run.Continuation--;
            run.Runs.RemoveAt(run.Runs.Count - 1);
            (run.RunId, run.OutDirectory) = (previousRun, previousOut);
            var (back, backRuns, backCount) = (run.RunId, run.Runs.ToList(), run.Continuation);
            state?.Update(task.Id, x => x with { Phase = TaskPhase.Finished, RunId = back, Container = null, Continuation = backCount, Runs = backRuns });
            return false;
        }
        run.Containers[run.RunId] = run.Container;
        return true;
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

    /// <summary>The session's start as the step <c>session/&lt;task&gt;/&lt;run&gt;</c> (ADR 0061). A step that runs again first adopts or removes the container an earlier attempt left; the
    /// workspace and task file steps inside it that finished are not repeated.</summary>
    private async Task<string> StartSessionAsync(ResolvedTask task, string runId, string branch, string goal, int maxTurns, int maxMinutes, (long Tokens, decimal? Usd) left, decimal spentUsd,
        string? seed, DateTimeOffset sessionStarted, string detail, Activity? span, string traceId, BudgetChoice? budget, CancellationToken ct)
    {
        if (steps is null)
            return await CreateSessionAsync(task, runId, branch, goal, maxTurns, maxMinutes, left, spentUsd, seed, sessionStarted, detail, span, traceId, budget, ct);
        // A run id gets one start, so a completed key is never asked for again.
        var container = $"chargehand-{runId}";
        await steps.RunAsync(StepKey.Session(settings.BatchId, task.Id, runId), async (attempt, token) =>
            container = attempt.Repeated && await AdoptAsync(task.Id, runId, token) is { } adopted
                ? adopted
                : await CreateSessionAsync(task, runId, branch, goal, maxTurns, maxMinutes, left, spentUsd, seed, sessionStarted, detail, span, traceId, budget, token), ct);
        return container;
    }

    /// <summary>The session container an earlier start of <paramref name="runId"/> left, when it is the session: running, or ended with its outcome. One that was made and never started, or
    /// ended without an outcome, is removed so the start can run again under its fixed name. One that ran may have left commits in the work volume, so its workspace step is reset and the volume
    /// removed: the start prepares it fresh. The reset goes first, so a cut after it still finds the container and does it again.</summary>
    /// <returns>The container to wait for; null: start one.</returns>
    private async Task<string?> AdoptAsync(string taskId, string runId, CancellationToken ct)
    {
        var name = $"chargehand-{runId}";
        var state = await Tolerant(taskId, () => engine.InspectAsync(name, ct), ct);
        if (state.Status == ContainerStatus.Missing)
            return null;
        if (state.Status == ContainerStatus.Running || state.Status == ContainerStatus.Exited && await HasOutcomeAsync(taskId, runId, ct))
            return name;
        if (state.Status == ContainerStatus.Exited)
        {
            await steps!.ResetAsync(StepKey.Prep(settings.BatchId, runId), ct);
            await RemoveSessionAsync(runId, name, ct);
            return null;
        }
        await engine.RemoveAsync(name, ct);
        return null;
    }

    /// <summary>The session's container, workspace and task file, up to the container's start.</summary>
    private async Task<string> CreateSessionAsync(ResolvedTask task, string runId, string branch, string goal, int maxTurns, int maxMinutes, (long Tokens, decimal? Usd) left, decimal spentUsd,
        string? seed, DateTimeOffset sessionStarted, string detail, Activity? span, string traceId, BudgetChoice? budget, CancellationToken ct)
    {
        var preset = settings.Preset;
        await engine.CreateVolumeAsync(RunnerNames.Out(runId), runId, ct);
        await workspace.PrepareWorkspaceAsync(new WorkspaceSpec(runId, settings.Image, settings.SourcePath, RunnerNames.Work(runId), branch, settings.BaseCommit), ct);
        if (seed is not null)
            await volumes.WriteSeedAsync(runId, seed, ct);
        await volumes.WriteTaskAsync(runId, new SessionTask(runId, goal, branch, maxTurns, maxMinutes, preset.NoProgressMinutes * 60, left.Tokens, MaxUsd: left.Usd, Prices: settings.Prices,
            Governor: settings.Governor?.ToSession(task.Size, task.BudgetUsd, spentUsd, budget), Environment: settings.Shared?.ForSession()), ct);

        var env = ModelEnvironment(runId, maxMinutes, left, sessionStarted);
        AddServiceEnvironment(env, task, runId, maxMinutes, left, sessionStarted, span, traceId);
        var container = await engine.StartAsync(new ContainerSpec(runId, settings.Image, RunnerNames.Work(runId), RunnerNames.Out(runId), settings.Network.Network, env,
            preset.MemoryMb, preset.Cpus, preset.Pids, ["session"], Caches: settings.Shared?.Caches is { Count: > 0 } caches ? caches : null), ct);
        state?.Update(task.Id, e => e with { Container = container });
        Publish(task.Id, RunEventKind.ContainerStarted, detail, s => s with { Branch = branch });
        return container;
    }

    /// <summary>The proxy, the run id and the model credential or run token.</summary>
    private Dictionary<string, string> ModelEnvironment(string runId, int maxMinutes, (long Tokens, decimal? Usd) left, DateTimeOffset sessionStarted)
    {
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
        return env;
    }

    /// <summary>The batch's shared services, the OTLP route and the chargehand server's MCP address.</summary>
    private void AddServiceEnvironment(Dictionary<string, string> env, ResolvedTask task, string runId, int maxMinutes, (long Tokens, decimal? Usd) left, DateTimeOffset sessionStarted, Activity? span,
        string traceId)
    {
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

    private static SessionOutcome Total(SessionOutcome? earlier, SessionOutcome last)
    {
        if (earlier is null)
            return last;
        var models = Models(earlier, last);
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

    private static Dictionary<string, TokenCounts> Models(SessionOutcome earlier, SessionOutcome last)
    {
        Dictionary<string, TokenCounts> models = new(earlier.ModelUsage ?? new Dictionary<string, TokenCounts>());
        foreach (var (name, now) in last.ModelUsage ?? new Dictionary<string, TokenCounts>())
            models[name] = models.TryGetValue(name, out var before)
                ? new TokenCounts(before.Input + now.Input, before.Output + now.Output, before.Reasoning + now.Reasoning, before.CacheRead + now.CacheRead, before.CacheWrite + now.CacheWrite)
                : now;
        return models;
    }

    private async Task RemoveSessionAsync(string runId, string? container, CancellationToken ct)
    {
        await engine.RemoveAsync(container ?? $"chargehand-{runId}", ct);
        await engine.RemoveVolumeAsync(RunnerNames.Work(runId), ct);
    }
}
