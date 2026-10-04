using System.Diagnostics;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Containers;
using Chargehand.Contracts;
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
/// <param name="ModelEnvironment">The model credential as the session container's environment (the fallback delivery; recorded as such on the batch result).</param>
/// <param name="McpUrl">The chargehand server's MCP address on the batch network; null: the session gets no research or review calls.</param>
public sealed record TaskRunnerSettings(string BatchId, RunRequest Request, string Image, string SourcePath, string BaseCommit, string RemoteUrl, string BaseBranch,
    BatchNetworkInfo Network, string? McpUrl, DrivenPreset Preset, TaskLimits Limits, bool Priced, IReadOnlyDictionary<string, string> ModelEnvironment, PushCredential Push,
    string ScratchRoot, string Model, string ClaudeVersion, bool AllowLocalRemote = false);

/// <summary>Runs one task of a batch to its end (ADR 0039): a workspace at the pinned commit, a session container on the batch network, then the handover (chargehand's own
/// verification, the scan, the push, a draft pull request) and the task's own <c>result/v1</c> in the run log. A cancel signals the session and ends the task; it never reaches
/// the handover, so nothing is pushed.</summary>
public sealed class TaskRunner(IContainerEngine engine, IWorkspaceEngine workspace, ISessionVolumes volumes, IBranchHandover handover, IRunLog log, TaskTokenMinter mint,
    TaskRunnerSettings settings, IEvidenceResolver resolver,
    Func<ResultContract, EvidenceScope, CancellationToken, Task<ResultContract>>? supportCheck, Func<ResultContract, ResultContract>? sign,
    TimeSpan? poll = null, TimeSpan? stopGrace = null) : ITaskRunner
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
        try
        {
            Directory.CreateDirectory(outDirectory);
            await engine.CreateVolumeAsync(RunnerNames.Out(runId), runId, ct);
            await workspace.PrepareWorkspaceAsync(new WorkspaceSpec(runId, settings.Image, settings.SourcePath, RunnerNames.Work(runId), branch, settings.BaseCommit), ct);
            await volumes.WriteTaskAsync(runId, new SessionTask(runId, task.Goal, branch, preset.MaxTurns, preset.MaxMinutes, preset.NoProgressMinutes * 60,
                settings.Limits.MaxTokens, MaxUsd: settings.Limits.MaxUsd), ct);

            Dictionary<string, string> env = new(settings.ModelEnvironment, StringComparer.Ordinal)
            {
                ["HTTPS_PROXY"] = settings.Network.ProxyUrl(runId),
                ["HTTP_PROXY"] = settings.Network.ProxyUrl(runId),
                ["CHARGEHAND_RUN_ID"] = runId,
            };
            if (settings.McpUrl is { } mcp)
            {
                env["CHARGEHAND_MCP_URL"] = mcp;
                // The token admits only this path and commit; /work is not it, so the session is told them.
                env["CHARGEHAND_REPOSITORY_PATH"] = settings.SourcePath;
                env["CHARGEHAND_BASE_COMMIT"] = settings.BaseCommit;
                // The forward is plain HTTP to the egress container; the proxy variables would send it to that same proxy, which answers CONNECT only.
                env["NO_PROXY"] = env["no_proxy"] = settings.Network.ProxyHost;
                env["CHARGEHAND_RUN_TOKEN"] = mint(new TaskGrant(runId, settings.SourcePath, settings.BaseCommit, settings.Limits.MaxUsd, settings.Limits.MaxTokens,
                    started.AddMinutes(preset.MaxMinutes + 15)));
            }
            container = await engine.StartAsync(new ContainerSpec(runId, settings.Image, RunnerNames.Work(runId), RunnerNames.Out(runId), settings.Network.Network, env,
                preset.MemoryMb, preset.Cpus, preset.Pids, ["session"]), ct);
            var state = await WaitAsync(container, runId, preset, progress, ct);

            await volumes.FetchOutputAsync(runId, outDirectory, ct);
            var session = ReadOutcome(outDirectory) ?? new SessionOutcome(SessionStatus.Failed, state.OomKilled ? "out_of_memory" : "no_outcome", 0, 0, 0, 0, null, state.ExitCode, [], false);
            var reportFile = Path.Combine(outDirectory, "driven-report.json");
            var reportJson = File.Exists(reportFile) ? await File.ReadAllTextAsync(reportFile, ct) : null;
            progress.Report(new TaskUsage(session.InputTokens + session.OutputTokens, settings.Priced ? session.CostUsd ?? 0 : 0));

            HandoverOutcome? handed = null;
            if (session.Status == SessionStatus.Completed && reportJson is not null && DrivenReport.Parse(reportJson) is { } report)
                handed = await handover.RunAsync(new HandoverInput(runId, outDirectory, settings.SourcePath, settings.BaseCommit, branch, settings.RemoteUrl, settings.BaseBranch,
                    Title(task.Goal), $"{report.Summary}\n\nTask: {task.Id}", settings.ScratchRoot, settings.Push, settings.Request.Context.Verify,
                    [.. settings.ModelEnvironment.Values], AllowLocalRemote: settings.AllowLocalRemote), ct);

            var scratchClone = Path.Combine(settings.ScratchRoot, runId);
            var traceId = ActivityTraceId.CreateRandom().ToHexString();
            var children = (await log.ListAsync(new RunListQuery(Since: started, Limit: 200), ct)).Where(r => r.ParentRunId == runId).ToList();
            var result = await DrivenResult.BuildTaskAsync(new DrivenTaskInput(task.Id, runId, traceId, settings.Model, settings.ClaudeVersion, session, reportJson, handed, children,
                Directory.Exists(scratchClone) ? scratchClone : settings.SourcePath, settings.BaseCommit, settings.Priced), resolver, supportCheck, ct);
            if (sign is not null)
                result = sign(result);
            await log.AppendAsync(new StartRecord(runId, started, traceId, settings.Request with { Text = task.Goal, Driven = null }, Environment.ProcessId, settings.BatchId), CancellationToken.None);
            await log.AppendAsync(new RunRecord(runId, started, DateTimeOffset.UtcNow, settings.Request.Context.Preset, null, null, result), CancellationToken.None);
            return Outcome(task, runId, result, session, handed, settings.Priced);
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
    private async Task<ContainerState> WaitAsync(string container, string runId, DrivenPreset preset, IProgress<TaskUsage> progress, CancellationToken ct)
    {
        var every = poll ?? DefaultPoll;
        var clock = Stopwatch.StartNew();
        var usageClock = Stopwatch.StartNew();
        long reported = 0;
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
                    if (await ReadUsageQuietly(runId, ct) is { } usage && usage.Tokens > reported)
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
