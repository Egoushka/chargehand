using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Chargehand.Budget;
using Chargehand.Config;
using Chargehand.Containers;
using Chargehand.Contracts;
using Chargehand.Egress;
using Chargehand.Results;
using Chargehand.RunLog;
using Chargehand.Verification;

namespace Chargehand.Driven;

/// <summary>How a session's output volume is written and read (ADR 0039): the task file goes in before the container starts, and after it ends the bundle, the report
/// and the outcome come out into a directory chargehand owns.</summary>
public interface ISessionVolumes
{
    Task WriteTaskAsync(string runId, SessionTask task, CancellationToken ct);

    /// <summary>Copies <c>chargehand.bundle</c>, <c>driven-report.json</c> and <c>session-outcome.json</c> (each only if it exists) from the volume into <paramref name="directory"/>.</summary>
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
public sealed record TaskRunnerSettings(string BatchId, RunRequest Request, string Image, string SourcePath, string BaseCommit, string RemoteUrl, string BaseBranch,
    BatchNetworkInfo Network, string? McpUrl, DrivenPreset Preset, TaskLimits Limits, bool Priced, IReadOnlyDictionary<string, string> ModelEnvironment, PushCredential Push,
    string ScratchRoot, string Model, string ClaudeVersion, bool AllowLocalRemote = false, string? ModelBaseUrl = null, bool CallbackForwards = false, DrivenOtlpRoute? Otlp = null,
    IReadOnlyDictionary<string, ModelPrice>? Prices = null, bool UsageOnSpans = false, string? GatewayKey = null);

/// <summary>Runs one task of a batch to its end (ADR 0039): a workspace at the pinned commit, a session container on the batch network, then the handover (chargehand's own
/// verification, the scan, the push, a draft pull request) and the task's own <c>result/v1</c> in the run log. A cancel signals the session and ends the task; it never reaches
/// the handover, so nothing is pushed. <paramref name="publish"/> gets the task's steps as events of the batch run: <c>container_started</c>, <c>session_progress</c> when the
/// session's tally changes, and the handover's <c>verify_finished</c>, <c>pushed</c> and <c>pr_opened</c>.</summary>
public sealed class TaskRunner(IContainerEngine engine, IWorkspaceEngine workspace, ISessionVolumes volumes, IBranchHandover handover, IRunLog log, TaskTokenMinter mint,
    TaskRunnerSettings settings, IEvidenceResolver resolver,
    Func<ResultContract, EvidenceScope, CancellationToken, Task<ResultContract>>? supportCheck, Func<ResultContract, ResultContract>? sign,
    TimeSpan? poll = null, TimeSpan? stopGrace = null, Action<RunStatus>? publish = null) : ITaskRunner
{
    private static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultUsagePoll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WallClockSlack = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions OutcomeJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<TaskOutcome> RunAsync(ResolvedTask task, IProgress<TaskUsage> progress, CancellationToken ct)
    {
        var runId = Orchestrator.NewRunId();
        var branch = $"chargehand/{runId}";
        var started = DateTimeOffset.UtcNow;
        var preset = settings.Preset;
        var outDirectory = Path.Combine(settings.ScratchRoot, "out", runId);
        string? container = null;
        // A child of the batch's span: the task's result carries the batch's trace id, so it resolves in Langfuse.
        using var span = Telemetry.Source.StartActivity(DrivenTelemetry.TaskSpan);
        span?.SetTag("chargehand.run_id", runId);
        span?.SetTag("chargehand.task.id", task.Id);
        span?.SetTag("chargehand.task.ref", task.Ref);
        var traceId = span?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString();
        try
        {
            Directory.CreateDirectory(outDirectory);
            await engine.CreateVolumeAsync(RunnerNames.Out(runId), runId, ct);
            await workspace.PrepareWorkspaceAsync(new WorkspaceSpec(runId, settings.Image, settings.SourcePath, RunnerNames.Work(runId), branch, settings.BaseCommit), ct);
            await volumes.WriteTaskAsync(runId, new SessionTask(runId, task.Goal, branch, preset.MaxTurns, preset.MaxMinutes, preset.NoProgressMinutes * 60,
                settings.Limits.MaxTokens, MaxUsd: settings.Limits.MaxUsd), ct);

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
                env["CLAUDE_CODE_OAUTH_TOKEN"] = ModelTokens.Mint(settings.GatewayKey!, runId, started.AddMinutes(preset.MaxMinutes + 15), settings.Limits.MaxTokens);
                env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
            }
            if (settings.CallbackForwards || gateway)
                // A forward and the model endpoint are plain HTTP to the egress container; the proxy variables would send them to that same proxy, which answers CONNECT only.
                env["NO_PROXY"] = env["no_proxy"] = $"{settings.Network.ProxyHost},{ContainerTemplate.CallbackAlias}";
            if (settings.Otlp is { } otlp)
                foreach (var (name, value) in otlp.Environment(settings.BatchId, task.Id, runId, span is null ? null : traceId))
                    env[name] = value;
            if (settings.McpUrl is { } mcp)
            {
                env["CHARGEHAND_MCP_URL"] = mcp;
                // The token admits only this path and commit; /work is not it, so the session is told them.
                env["CHARGEHAND_REPOSITORY_PATH"] = settings.SourcePath;
                env["CHARGEHAND_BASE_COMMIT"] = settings.BaseCommit;
                env["CHARGEHAND_RUN_TOKEN"] = mint(new TaskGrant(runId, settings.SourcePath, settings.BaseCommit, settings.Limits.MaxUsd, settings.Limits.MaxTokens,
                    started.AddMinutes(preset.MaxMinutes + 15)));
            }
            container = await engine.StartAsync(new ContainerSpec(runId, settings.Image, RunnerNames.Work(runId), RunnerNames.Out(runId), settings.Network.Network, env,
                preset.MemoryMb, preset.Cpus, preset.Pids, ["session"]), ct);
            Publish(task.Id, RunEventKind.ContainerStarted, "session container started");
            var sessionStarted = DateTimeOffset.UtcNow;
            var state = await WaitAsync(container, runId, task.Id, preset, progress, ct);
            var sessionEnded = DateTimeOffset.UtcNow;

            await volumes.FetchOutputAsync(runId, outDirectory, ct);
            var session = ReadOutcome(outDirectory) ?? new SessionOutcome(SessionStatus.Failed, state.OomKilled ? "out_of_memory" : "no_outcome", 0, 0, 0, 0, null, state.ExitCode, [], false);
            using (var call = Telemetry.Source.StartActivity(DrivenTelemetry.SessionSpan, ActivityKind.Client, span?.Context ?? default, startTime: sessionStarted))
            {
                DrivenTelemetry.TagSession(call, session, settings.Prices ?? new Dictionary<string, ModelPrice>(), settings.UsageOnSpans);
                call?.SetEndTime(sessionEnded.UtcDateTime);
            }
            var reportFile = Path.Combine(outDirectory, "driven-report.json");
            var reportJson = File.Exists(reportFile) ? await File.ReadAllTextAsync(reportFile, ct) : null;
            progress.Report(new TaskUsage(session.InputTokens + session.OutputTokens, settings.Priced ? session.CostUsd ?? 0 : 0));

            HandoverOutcome? handed = null;
            if (session.Status == SessionStatus.Completed && reportJson is not null && DrivenReport.Parse(reportJson) is { } report)
                handed = await handover.RunAsync(new HandoverInput(runId, outDirectory, settings.SourcePath, settings.BaseCommit, branch, settings.RemoteUrl, settings.BaseBranch,
                    Title(task.Goal), $"{report.Summary}\n\nTask: {task.Id}", settings.ScratchRoot, settings.Push, settings.Request.Context.Verify,
                    [.. settings.ModelEnvironment.Values], AllowLocalRemote: settings.AllowLocalRemote,
                    Report: e => Publish(task.Id, e.Kind, e.Detail, s => s with { PrUrl = e.PrUrl, Branch = e.Kind == RunEventKind.Pushed ? branch : null })), ct);

            var scratchClone = Path.Combine(settings.ScratchRoot, runId);
            var children = (await log.ListAsync(new RunListQuery(Since: started, Limit: 200), ct)).Where(r => r.ParentRunId == runId).ToList();
            var result = await DrivenResult.BuildTaskAsync(new DrivenTaskInput(task.Id, runId, traceId, settings.Model, settings.ClaudeVersion, session, reportJson, handed, children,
                Directory.Exists(scratchClone) ? scratchClone : settings.SourcePath, settings.BaseCommit, settings.Priced), resolver, supportCheck, ct);
            if (sign is not null)
                result = sign(result);
            await log.AppendAsync(new StartRecord(runId, started, traceId, settings.Request with { Text = task.Goal, Driven = null }, Environment.ProcessId, settings.BatchId), CancellationToken.None);
            await log.AppendAsync(new RunRecord(runId, started, DateTimeOffset.UtcNow, settings.Request.Context.Preset, null, null, result), CancellationToken.None);
            var outcome = Outcome(task, runId, result, session, handed, settings.Priced);
            Tag(span, outcome, session, started);
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
            // A start cancelled in flight can leave the container docker already made; the name is fixed, so remove it by name.
            await engine.RemoveAsync(container ?? $"chargehand-{runId}", CancellationToken.None);
            await engine.RemoveVolumeAsync(RunnerNames.Work(runId), CancellationToken.None);
            DeleteQuietly(outDirectory);
            DeleteQuietly(Path.Combine(settings.ScratchRoot, runId));
        }
    }

    /// <summary>Waits for the container to end. A cancel interrupts the session, gives it a moment, then kills it, and rethrows; a session past its wall clock plus a margin is killed.</summary>
    private async Task<ContainerState> WaitAsync(string container, string runId, string taskId, DrivenPreset preset, IProgress<TaskUsage> progress, CancellationToken ct)
    {
        var every = poll ?? DefaultPoll;
        var clock = Stopwatch.StartNew();
        var usageClock = Stopwatch.StartNew();
        long reported = 0;
        TaskUsage? shown = null;
        try
        {
            while (true)
            {
                var state = await engine.InspectAsync(container, ct);
                if (state.Status != ContainerStatus.Running)
                    return state;
                if (clock.Elapsed > TimeSpan.FromMinutes(preset.MaxMinutes) + WallClockSlack)
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
                        Publish(taskId, RunEventKind.SessionProgress, Describe(usage), s => s with { Tokens = usage.Tokens, Turns = usage.Turns, Stage = usage.Stage });
                    }
                    if (usage is not null && usage.Tokens > reported)
                    {
                        reported = usage.Tokens;
                        progress.Report(new TaskUsage(usage.Tokens, 0));
                    }
                }
                await Task.Delay(every, ct);
            }
        }
        catch (OperationCanceledException)
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

    private static TaskOutcome Outcome(ResolvedTask task, string runId, ResultContract result, SessionOutcome session, HandoverOutcome? handed, bool priced)
    {
        var state = result.Status switch
        {
            ResultStatus.Completed => TaskState.Completed,
            ResultStatus.NeedsInput => TaskState.NeedsInput,
            _ => TaskState.Failed,
        };
        var pushed = handed?.Status is HandoverStatus.Pushed or HandoverStatus.PrFailed;
        return new TaskOutcome(task.Id, state, runId, state == TaskState.Completed ? null : result.Summary, result.Error?.Code,
            priced ? session.CostUsd ?? 0 : 0, session.InputTokens + session.OutputTokens, pushed ? handed!.Branch : null, handed?.PullRequest?.Url);
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
