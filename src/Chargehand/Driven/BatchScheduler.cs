using Chargehand.Contracts;

namespace Chargehand.Driven;

/// <summary>Runs a batch's tasks under its limits (ADR 0039): at most <c>MaxParallel</c> at once, tasks in order, and a task starts only if the
/// batch could still afford it. "Afford" reserves each running task's whole cap, so two tasks at 2000 tokens each against a 5000 cap leave no
/// room for a third until they finish and their real spend replaces the reservation. A batch that ends up over its cap by more than one task's
/// cap while tasks are still running has them cancelled; so has a task that reports more than its own cap (live usage reaches the scheduler while the task
/// runs, so the caps bind before it ends). A rate-limited or unavailable provider stops new starts and says how to switch to an
/// API key; nothing switches by itself. A halt (the token) cancels what runs and starts nothing more.</summary>
public sealed class BatchScheduler(ITaskRunner runner)
{
    public const string RateLimitedAction =
        "The subscription is rate limited or unavailable, so the rest of the batch was not started. Retry later, or switch the profile to an API key "
        + "(claude_code.api_key_secret in place of oauth_token_secret) and resend the tasks that did not finish.";

    private sealed class Running(TaskLimits limits, CancellationTokenSource cts)
    {
        public TaskLimits Limits { get; } = limits;
        public CancellationTokenSource Cts { get; } = cts;
        public TaskUsage Reported { get; set; } = new(0, 0);
        public Task<TaskOutcome>? Task { get; set; }
        public bool OverTaskCap { get; set; }
    }

    public async Task<BatchOutcome> RunAsync(IReadOnlyList<ResolvedTask> tasks, BatchLimits limits, CancellationToken ct)
    {
        var outcomes = new TaskOutcome?[tasks.Count];
        var running = new Dictionary<int, Running>();
        var gate = new object();
        var changed = new SemaphoreSlim(0);
        long doneTokens = 0;
        decimal doneUsd = 0;
        string? notStartedReason = null;
        ErrorCode? notStartedError = null;
        string? action = null;
        var overspent = false;
        var next = 0;

        bool Fits()
        {
            lock (gate)
            {
                if (limits.MaxTokensTotal is { } tokenCap)
                {
                    var committed = doneTokens + running.Values.Sum(r => Math.Max(r.Limits.MaxTokens, r.Reported.Tokens));
                    if (committed + limits.PerTask.MaxTokens > tokenCap)
                        return false;
                }
                if (limits.MaxUsdTotal is { } usdCap && limits.PerTask.MaxUsd is { } perTask)
                {
                    var committed = doneUsd + running.Values.Sum(r => Math.Max(r.Limits.MaxUsd ?? 0, r.Reported.Usd));
                    if (committed + perTask > usdCap)
                        return false;
                }
                return true;
            }
        }

        void CheckTaskCap(Running r)
        {
            lock (gate)
            {
                if (r.OverTaskCap || (r.Reported.Tokens <= r.Limits.MaxTokens && (r.Limits.MaxUsd is not { } cap || r.Reported.Usd <= cap)))
                    return;
                r.OverTaskCap = true;
                r.Cts.Cancel();
            }
        }

        void CheckOverspend()
        {
            lock (gate)
            {
                var tokens = doneTokens + running.Values.Sum(r => r.Reported.Tokens);
                var usd = doneUsd + running.Values.Sum(r => r.Reported.Usd);
                var over = (limits.MaxTokensTotal is { } t && tokens > t)
                    || (limits.MaxUsdTotal is { } u && usd > u);
                if (!over || overspent)
                    return;
                overspent = true;
                notStartedReason ??= "the batch's cap was used up";
                notStartedError ??= ErrorCode.CostCapReached;
                foreach (var r in running.Values)
                    r.Cts.Cancel();
            }
        }

        while (true)
        {
            // Start what fits, in order.
            while (next < tasks.Count && notStartedReason is null && !ct.IsCancellationRequested && running.Count < limits.MaxParallel && Fits())
            {
                if (limits.Global is not null && !await limits.Global.WaitAsync(0, CancellationToken.None))
                    break;
                var index = next++;
                var task = tasks[index];
                var state = new Running(limits.PerTask, CancellationTokenSource.CreateLinkedTokenSource(ct));
                lock (gate)
                    running[index] = state;
                var progress = new SyncProgress(u =>
                {
                    lock (gate)
                        state.Reported = u;
                    CheckTaskCap(state);
                    CheckOverspend();
                    changed.Release();
                });
                // Called directly, not on the thread pool: it runs up to its first await here, so tasks begin in order.
                state.Task = Execute(task, progress, state.Cts);
            }

            if (running.Count == 0)
            {
                if (next < tasks.Count && notStartedReason is null && !ct.IsCancellationRequested)
                {
                    if (!Fits())
                    {
                        notStartedReason = "the batch's cap leaves no room for another task";
                        notStartedError = ErrorCode.CostCapReached;
                    }
                    else
                    {
                        // Only the shared limit is in the way (another batch holds the slots): look again shortly.
                        await Task.Delay(100, CancellationToken.None);
                        continue;
                    }
                }
                break;
            }

            // Wait for a task to finish or report.
            var finished = Task.WhenAny(running.Values.Select(r => r.Task!));
            await Task.WhenAny(finished, changed.WaitAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None));
            foreach (var (index, state) in running.ToList())
            {
                if (!state.Task!.IsCompleted)
                    continue;
                var outcome = await state.Task;
                if (outcome.Tokens == 0 && outcome.Usd == 0)
                    outcome = outcome with { Tokens = state.Reported.Tokens, Usd = state.Reported.Usd };
                if (state.OverTaskCap && outcome.State != TaskState.Completed)
                    outcome = outcome with { State = TaskState.Failed, Error = ErrorCode.CostCapReached, Detail = "stopped: the task's cap was exceeded" };
                lock (gate)
                {
                    running.Remove(index);
                    doneTokens += outcome.Tokens;
                    doneUsd += outcome.Usd;
                }
                limits.Global?.Release();
                state.Cts.Dispose();
                if (overspent && outcome.State != TaskState.Completed)
                    outcome = outcome with { Error = ErrorCode.CostCapReached, Detail = "stopped: the batch's cap was exceeded" };
                outcomes[index] = outcome;
                if (outcome.Error is ErrorCode.RateLimited or ErrorCode.ProviderUnavailable && notStartedReason is null)
                {
                    notStartedReason = "not started: the provider is rate limited or unavailable";
                    action = RateLimitedAction;
                }
            }
        }

        var why = notStartedReason ?? (ct.IsCancellationRequested ? "not started: the batch was cancelled" : "not started");
        for (var i = 0; i < tasks.Count; i++)
            outcomes[i] ??= new TaskOutcome(tasks[i].Id, TaskState.NotStarted, null, why.StartsWith("not started", StringComparison.Ordinal) ? why : $"not started: {why}", notStartedError);
        return new BatchOutcome([.. outcomes.Select(o => o!)], action);
    }

    private async Task<TaskOutcome> Execute(ResolvedTask task, IProgress<TaskUsage> progress, CancellationTokenSource cts)
    {
        try
        {
            return await runner.RunAsync(task, progress, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return new TaskOutcome(task.Id, TaskState.Cancelled, null, "cancelled");
        }
        catch (Exception e)
        {
            var error = ChargehandException.ErrorOf(e);
            return new TaskOutcome(task.Id, TaskState.Failed, null, error.Message, error.Code);
        }
    }

    /// <summary>Calls back on the reporting thread; unlike <see cref="Progress{T}"/> it does not post to a captured context.</summary>
    private sealed class SyncProgress(Action<TaskUsage> report) : IProgress<TaskUsage>
    {
        public void Report(TaskUsage value) => report(value);
    }
}
