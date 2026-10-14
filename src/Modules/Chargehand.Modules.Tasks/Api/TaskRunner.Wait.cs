using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Modules.Tasks.Api;

/// <summary>A running session's container: waited for, its usage polled and published, and every runner call tolerant of a runner that stops answering (ADR 0058).</summary>
public sealed partial class TaskRunner
{
    /// <summary>Waits for the container to end. A cancel interrupts the session, gives it a moment, then kills it, and rethrows; a session past its wall clock plus a margin is killed.</summary>
    private async Task<ContainerState> WaitAsync(string container, string runId, string taskId, int maxMinutes, IProgress<TaskUsage> progress, DateTimeOffset since, SessionOutcome? prior, CancellationToken ct)
    {
        var every = poll ?? DefaultPoll;
        var usageClock = Stopwatch.StartNew();
        var seen = new UsagePoll(prior, settings.Priced);
        try
        {
            while (true)
            {
                var state = await Tolerant(taskId, () => engine.InspectAsync(container, ct), ct);
                var peak = NotePeak(taskId, state);
                if (state.Status != ContainerStatus.Running)
                    return state;
                if (DateTimeOffset.UtcNow - since > TimeSpan.FromMinutes(maxMinutes) + WallClockSlack)
                {
                    await engine.SignalAsync(container, "SIGKILL", CancellationToken.None);
                    var killed = await engine.InspectAsync(container, CancellationToken.None);
                    NotePeak(taskId, killed);
                    return killed;
                }
                if (usageClock.Elapsed >= (poll ?? DefaultUsagePoll))
                {
                    usageClock.Restart();
                    await PollUsageAsync(seen, runId, taskId, peak, progress, ct);
                }
                await Task.Delay(every, ct);
            }
        }
        catch (OperationCanceledException) when (!Detaching)
        {
            await InterruptAsync(container, every);
            throw;
        }
    }

    private async Task InterruptAsync(string container, TimeSpan every)
    {
        await engine.SignalAsync(container, "SIGINT", CancellationToken.None);
        var grace = Stopwatch.StartNew();
        while (grace.Elapsed < (stopGrace ?? DefaultGrace) && (await engine.InspectAsync(container, CancellationToken.None)).Status == ContainerStatus.Running)
            await Task.Delay(every, CancellationToken.None);
        await engine.SignalAsync(container, "SIGKILL", CancellationToken.None);
    }

    /// <summary>The session's usage as polled so far. A continuation's tally starts at zero; the task's numbers are the earlier sessions' plus it.</summary>
    private sealed class UsagePoll(SessionOutcome? prior, bool priced)
    {
        public long EarlierTokens { get; } = prior is null ? 0 : prior.InputTokens + prior.OutputTokens;

        public decimal EarlierUsd { get; } = priced ? prior?.CostUsd ?? 0 : 0;

        private int EarlierTurns { get; } = prior?.Turns ?? 0;

        public long Reported { get; set; }

        public TaskUsage? Shown { get; set; }

        public TaskUsage Whole(TaskUsage usage) => usage with
        {
            Tokens = EarlierTokens + usage.Tokens,
            Usd = EarlierUsd,
            Turns = usage.Turns is { } turns ? EarlierTurns + turns : null,
            UsdEstimate = usage.UsdEstimate is { } estimate ? estimate + (prior?.UsdEstimate ?? 0) : null,
            CacheRead = usage.CacheRead is { } read ? read + (prior?.CacheReadTokens ?? 0) : null,
            CacheWrite = usage.CacheWrite is { } write ? write + (prior?.CacheWriteTokens ?? 0) : null,
            UsdEstimatePartial = usage.UsdEstimatePartial || prior is { UsdEstimatePartial: true }
        };
    }

    private async Task PollUsageAsync(UsagePoll seen, string runId, string taskId, int? peak, IProgress<TaskUsage> progress, CancellationToken ct)
    {
        var usage = await ReadUsageQuietly(runId, ct);
        if (usage is not null && usage != seen.Shown)
        {
            seen.Shown = usage;
            var whole = seen.Whole(usage);
            Publish(taskId, RunEventKind.SessionProgress, Describe(whole), s => s with { Tokens = whole.Tokens, Turns = whole.Turns, Stage = whole.Stage, UsdEstimate = whole.UsdEstimate, CacheRead = whole.CacheRead, CacheWrite = whole.CacheWrite, UsdEstimatePartial = whole.UsdEstimatePartial ? true : null, MemoryPeakMb = peak });
        }
        if (usage is not null && usage.Tokens > seen.Reported)
        {
            seen.Reported = usage.Tokens;
            progress.Report(Capped(seen.EarlierTokens + usage.Tokens, seen.EarlierUsd, ended: false));
        }
    }

    /// <summary>Keeps the largest peak the engine reported for the task; null while none was.</summary>
    private int? NotePeak(string taskId, ContainerState state)
    {
        if (state.MemoryPeakMb is { } mb)
            return _memoryPeaks.AddOrUpdate(taskId, mb, (_, seen) => Math.Max(seen, mb));
        return _memoryPeaks.TryGetValue(taskId, out var known) ? known : null;
    }

    /// <summary>The volumes of the runner a task ran on: this runner's own unless the task came from another one (ADR 0058).</summary>
    private ISessionVolumes VolumesOf(string? runner) => runner is null || volumesOf is null ? volumes : volumesOf(runner);

    private Task<object?> Tolerant(string? taskId, Func<Task> call, CancellationToken ct) => Tolerant<object?>(taskId, async () => { await call(); return null; }, ct);

    /// <summary>Runs a call to the runner, and when the runner does not answer (<see cref="RunnerUnreachableException"/>) asks again every poll until it has been silent for the outage grace
    /// (ADR 0058). A refusal is not waited out. A cancel ends the wait.</summary>
    private async Task<T> Tolerant<T>(string? taskId, Func<Task<T>> call, CancellationToken ct)
    {
        DateTimeOffset? silentSince = null;
        while (true)
        {
            try
            {
                return await call();
            }
            catch (RunnerUnreachableException e) when (!ct.IsCancellationRequested)
            {
                var grace = outageGrace ?? DefaultOutageGrace;
                if (silentSince is null && taskId is not null)
                    Publish(taskId, RunEventKind.SessionProgress, $"the runner does not answer ({e.Message}); waiting for it up to {grace.TotalMinutes:0.#} minutes");
                silentSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - silentSince > grace)
                    throw;
                await Task.Delay(poll ?? DefaultPoll, ct);
            }
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
}
