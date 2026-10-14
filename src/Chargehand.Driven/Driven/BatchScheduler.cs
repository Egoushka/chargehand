using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Driven;

/// <summary>Runs a batch's tasks under its limits (ADR 0039): at most <c>MaxParallel</c> at once, tasks in order, and a task starts only if the
/// batch could still afford it. "Afford" reserves each running task's whole cap, so two tasks at 2000 tokens each against a 5000 cap leave no
/// room for a third until they finish and their real spend replaces the reservation. A batch that ends up over its cap by more than one task's
/// cap while tasks are still running has them cancelled; so has a task that reports more than its own cap (live usage reaches the scheduler while the task
/// runs, so the caps bind before it ends). A task that ended on a usage or rate limit holds new starts until the limit lifts (its reset time, else a fixed backoff; a reset
/// further off than <see cref="MaxPause"/> stops the batch instead); running tasks go on. An unavailable provider stops new starts and says how to switch to an
/// API key; nothing switches by itself. With an <paramref name="admission"/>, a task also waits until the host has memory for its request (ADR 0045); a running task is
/// never stopped for it. A task with <c>DependsOn</c> starts only once all of those ended <c>completed</c>, with their branches in <see cref="ResolvedTask.Upstream"/> (ADR 0049); one whose
/// dependency failed, asked a question, was cancelled or was not started is <c>not_started</c> with that reason and holds no slot. A halt (the token) cancels what runs and starts nothing more.</summary>
/// <param name="backoff">How long a limit that names no reset holds new starts; <see cref="UsageLimit.DefaultBackoff"/> when null.</param>
/// <param name="admission">Shared by every batch of the server; null: tasks start by count and caps only. With several runners (ADR 0058) it also names the one a task runs on; a task whose runner
/// could not be reached before its session started (<see cref="TaskOutcome.Replace"/>) is asked for again, up to <see cref="MaxPlacements"/> times in all.</param>
/// <param name="taskRequestMb">What the host must have free for a task: the working set its repository's sessions have measured (<see cref="MemoryRequest"/>), not its container's limit.</param>
/// <param name="time">The clock rate-limit pauses and admission intervals are read on; <see cref="TimeProvider.System"/> when null.</param>
public sealed class BatchScheduler(ITaskRunner runner, TimeSpan? backoff = null, IAdmission? admission = null, int taskRequestMb = 0, BatchControl? control = null, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The longest wait for a limit to lift; past it the batch stops and the caller retries.</summary>
    public static readonly TimeSpan MaxPause = TimeSpan.FromHours(6);

    public const string RateLimitedAction =
        "The subscription is rate limited or unavailable, so the rest of the batch was not started. Retry later, or switch the profile to an API key "
        + "(claude_code.api_key_secret in place of oauth_token_secret) and resend the tasks that did not finish.";

    public const string RepeatedLimitAction =
        "The provider's usage or rate limit was hit twice in a row with no task finishing in between, so the rest of the batch was not started. "
        + "Resend the tasks that did not finish once the limit lifts.";

    /// <summary>How many runners a task is placed on before its runner's silence is the task's failure.</summary>
    public const int MaxPlacements = 3;

    private sealed class Running(TaskLimits limits, CancellationTokenSource cts)
    {
        public string? Runner { get; set; }
        public TaskLimits Limits { get; } = limits;
        public CancellationTokenSource Cts { get; } = cts;
        public TaskUsage Reported { get; set; } = new(0, 0);
        public Task<TaskOutcome>? Task { get; set; }
        public bool OverTaskCap { get; set; }
    }

    /// <param name="onFinished">Told each started task's final outcome as it ends, after the caps have had their say.</param>
    /// <param name="onHold">Told a task's id and why, when it first waits for the host and when it is let in against the rule.</param>
    /// <param name="onSkipped">Told the outcome of a task that will not start because a task it depends on did not complete.</param>
    /// <param name="onPause">Told a task's id and when the limit it hit lifts, when a usage or rate limit holds new starts.</param>
    public async Task<BatchOutcome> RunAsync(IReadOnlyList<ResolvedTask> tasks, BatchLimits limits, CancellationToken ct, Action<TaskOutcome>? onFinished = null,
        Action<string, string>? onHold = null, Action<TaskOutcome>? onSkipped = null, Action<string, DateTimeOffset>? onPause = null)
    {
        var outcomes = new TaskOutcome?[tasks.Count];
        var started = new bool[tasks.Count];
        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < tasks.Count; i++)
            indexOf[tasks[i].Id] = i;
        var running = new Dictionary<int, Running>();
        var gate = new object();
        var changed = new SemaphoreSlim(0);
        long doneTokens = 0;
        decimal doneUsd = 0;
        string? notStartedReason = null;
        ErrorCode? notStartedError = null;
        string? action = null;
        var overspent = false;
        var pausedUntil = DateTimeOffset.MinValue;
        var limits429 = 0;
        var limitStop = false;
        var admitAfter = DateTimeOffset.MinValue;
        string? holding = null;
        var placements = new int[tasks.Count];

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

        IEnumerable<int> Dependencies(int i) => (tasks[i].DependsOn ?? []).Where(indexOf.ContainsKey).Select(d => indexOf[d]);

        bool Pending(int i) => !started[i] && outcomes[i] is null;

        // The first task, in order, that is not started and whose dependencies all completed; null: none is ready now.
        int? NextReady()
        {
            for (var i = 0; i < tasks.Count; i++)
                if (Pending(i) && Dependencies(i).All(d => outcomes[d]?.State == TaskState.Completed))
                    return i;
            return null;
        }

        // A task whose dependency ended any way but completed will never start; that spreads down the chain.
        void SkipDependents()
        {
            bool again;
            do
            {
                again = false;
                for (var i = 0; i < tasks.Count; i++)
                {
                    if (!Pending(i))
                        continue;
                    if (control?.IsCancelled(tasks[i].Id) == true)
                    {
                        // An operator cancelled it before it started.
                        var stopped = new TaskOutcome(tasks[i].Id, TaskState.Cancelled, null, "cancelled by the operator before it started", ErrorCode.Cancelled);
                        outcomes[i] = stopped;
                        onSkipped?.Invoke(stopped);
                        again = true;
                        continue;
                    }
                    var cause = Dependencies(i).Select(d => outcomes[d]).FirstOrDefault(o => o is { State: not TaskState.Completed });
                    if (cause is null)
                        continue;
                    var how = cause.State switch
                    {
                        TaskState.Failed => "failed",
                        TaskState.NeedsInput => "asked a question",
                        TaskState.Cancelled => "was cancelled",
                        _ => "was not started",
                    };
                    var skipped = new TaskOutcome(tasks[i].Id, TaskState.NotStarted, null, $"not started: the task it depends on, '{cause.Id}', {how}", cause.State == TaskState.Cancelled ? ErrorCode.Cancelled : null);
                    outcomes[i] = skipped;
                    onSkipped?.Invoke(skipped);
                    again = true;
                }
            }
            while (again);
        }

        ResolvedTask WithUpstream(int i)
        {
            if (tasks[i].DependsOn is not { Count: > 0 })
                return tasks[i];
            var upstream = Dependencies(i).Select(d => outcomes[d]!).Where(o => o.RunId is not null && o.Branch is not null).Select(o => new UpstreamWork(o.Id, o.RunId!, o.Branch!, o.PrUrl, o.Runner));
            return tasks[i] with { Upstream = [.. upstream] };
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

        // Asks the host, at most once per interval; null: look again later. Else the runner the task goes to (null with one host).
        async Task<AdmissionDecision?> Admitted(int i)
        {
            var id = tasks[i].Id;
            if (_time.GetUtcNow() < admitAfter)
                return null;
            AdmissionDecision decision;
            try
            {
                decision = await admission!.TryAdmitAsync(taskRequestMb, tasks[i].Runner, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            if (decision.Admit)
            {
                holding = null;
                if (decision.Detail is { } forced)
                    onHold?.Invoke(id, forced);
                return decision;
            }
            if (decision.Refuse)
            {
                // Nothing will place it: the batch stops starting tasks, and what runs finishes.
                notStartedReason ??= $"not started: {decision.Detail}";
                notStartedError ??= ErrorCode.ContainerUnavailable;
                return null;
            }
            admitAfter = _time.GetUtcNow() + admission.Interval;
            if (holding != id)
                onHold?.Invoke(id, decision.Detail!);
            holding = id;
            return null;
        }

        while (true)
        {
            // A task an operator cancelled before it started never does.
            SkipDependents();
            // Start what fits, in order.
            while (notStartedReason is null && !ct.IsCancellationRequested && running.Count < limits.MaxParallel && _time.GetUtcNow() >= pausedUntil && NextReady() is { } index && Fits())
            {
                if (limits.Global is not null && !await limits.Global.WaitAsync(0, CancellationToken.None))
                    break;
                AdmissionDecision? placed = null;
                if (admission is not null && (placed = await Admitted(index)) is null)
                {
                    limits.Global?.Release();
                    break;
                }
                started[index] = true;
                var task = WithUpstream(index) with { Runner = placed?.Runner ?? tasks[index].Runner };
                var state = new Running(limits.PerTask, CancellationTokenSource.CreateLinkedTokenSource(ct)) { Runner = placed?.Runner };
                lock (gate)
                    running[index] = state;
                control?.Started(task.Id, state.Cts);
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
                if (Enumerable.Range(0, tasks.Count).Any(Pending) && notStartedReason is null && !ct.IsCancellationRequested)
                {
                    if (NextReady() is null)
                    {
                        // Every dependency has ended and none is waiting to start, so what is left depends in a circle (a request is checked for that before it gets here).
                        notStartedReason = "not started: the tasks it depends on can never complete";
                    }
                    else if (!Fits())
                    {
                        notStartedReason = "the batch's cap leaves no room for another task";
                        notStartedError = ErrorCode.CostCapReached;
                    }
                    else if (pausedUntil - _time.GetUtcNow() is { Ticks: > 0 } left)
                    {
                        // A usage or rate limit holds new starts: wait until it lifts, or for a halt.
                        await Task.Delay(left, _time, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                        continue;
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
                control?.Ended(outcome.Id);
                if (outcome.Replace)
                {
                    // Its runner did not answer before the session started: nothing ran, so the task takes no part in the caps and is placed again, elsewhere if it can be.
                    lock (gate)
                        running.Remove(index);
                    limits.Global?.Release();
                    admission?.Ended(state.Runner);
                    state.Cts.Dispose();
                    if (++placements[index] < MaxPlacements && !ct.IsCancellationRequested)
                    {
                        started[index] = false;
                        onHold?.Invoke(outcome.Id, outcome.Detail ?? "its runner did not answer; placing it again");
                        continue;
                    }
                    outcomes[index] = outcome with { State = TaskState.Failed, Replace = false };
                    onFinished?.Invoke(outcomes[index]!);
                    continue;
                }
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
                admission?.Ended(state.Runner);
                state.Cts.Dispose();
                if (overspent && outcome.State != TaskState.Completed)
                    outcome = outcome with { Error = ErrorCode.CostCapReached, Detail = "stopped: the batch's cap was exceeded" };
                outcomes[index] = outcome;
                onFinished?.Invoke(outcome);
                if (outcome.State == TaskState.Completed)
                    limits429 = 0;
                if (outcome.Error is ErrorCode.RateLimited && notStartedReason is null)
                {
                    // One limit pauses; a second with no task finished since means the pause did not help, so the batch ends and is resent after the reset.
                    if (++limits429 >= 2)
                    {
                        notStartedReason = "not started: the provider's rate limit was hit twice in a row";
                        action = RepeatedLimitAction;
                    }
                    else if (LimitLifts(outcome) is { } lifts)
                    {
                        pausedUntil = lifts > pausedUntil ? lifts : pausedUntil;
                        onPause?.Invoke(outcome.Id, pausedUntil);
                    }
                    else
                    {
                        notStartedReason = "not started: the provider is rate limited until later than the batch will wait";
                        action = RateLimitedAction;
                    }
                    if (notStartedReason is not null)
                    {
                        notStartedError = ErrorCode.RateLimited;
                        limitStop = true;
                    }
                }
                else if (outcome.Error is ErrorCode.ProviderUnavailable && notStartedReason is null)
                {
                    notStartedReason = "not started: the provider is rate limited or unavailable";
                    action = RateLimitedAction;
                }
            }
            SkipDependents();
        }

        var why = notStartedReason ?? (ct.IsCancellationRequested ? "not started: the batch was cancelled" : "not started");
        if (notStartedReason is null && ct.IsCancellationRequested)
            notStartedError = ErrorCode.Cancelled;
        for (var i = 0; i < tasks.Count; i++)
            outcomes[i] ??= new TaskOutcome(tasks[i].Id, TaskState.NotStarted, null, why.StartsWith("not started", StringComparison.Ordinal) ? why : $"not started: {why}", notStartedError);
        return new BatchOutcome([.. outcomes.Select(o => o!)], action, limitStop ? ErrorCode.RateLimited : null);
    }

    /// <summary>When new starts may resume after a rate-limited task: its reset time, but never sooner than the backoff (a reset that is unparsed, already past or too
    /// optimistic must not burn the batch); null when that is further off than <see cref="MaxPause"/>.</summary>
    private DateTimeOffset? LimitLifts(TaskOutcome outcome)
    {
        var now = _time.GetUtcNow();
        var floor = now + (backoff ?? UsageLimit.DefaultBackoff);
        var lifts = outcome.RetryAfter is { } reset && reset > floor ? reset : floor;
        return lifts - now > MaxPause ? null : lifts;
    }

    private async Task<TaskOutcome> Execute(ResolvedTask task, IProgress<TaskUsage> progress, CancellationTokenSource cts)
    {
        try
        {
            return await runner.RunAsync(task, progress, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return new TaskOutcome(task.Id, TaskState.Cancelled, null, "cancelled", ErrorCode.Cancelled);
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
