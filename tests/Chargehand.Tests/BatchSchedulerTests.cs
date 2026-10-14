using Chargehand.Contracts;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>ADR 0039: how many tasks of a batch run at once, and when the batch stops starting them (caps, a rate limit, a halt).</summary>
public class BatchSchedulerTests
{
    private static ResolvedTask[] Tasks(int n) => [.. Enumerable.Range(1, n).Select(i => new ResolvedTask($"t{i}", null, $"goal {i}"))];

    private static BatchLimits Limits(int parallel = 2, long? tokens = null, decimal? usd = null, long perTaskTokens = 1000, decimal? perTaskUsd = null, SemaphoreSlim? global = null) =>
        new(parallel, tokens, usd, new TaskLimits(perTaskTokens, perTaskUsd), global);

    /// <summary>A task runner whose tasks finish when the test says so, and which reports progress on demand.</summary>
    private sealed class ScriptedRunner : ITaskRunner
    {
        private readonly object _lock = new();
        public Dictionary<string, TaskCompletionSource<TaskOutcome>> Pending { get; } = [];
        public Dictionary<string, IProgress<TaskUsage>> Progress { get; } = [];
        public Dictionary<string, CancellationToken> Tokens { get; } = [];
        public List<string> Started { get; } = [];
        public Dictionary<string, ResolvedTask> Tasks { get; } = [];
        public int Running;
        public int MaxRunning;
        public Func<ResolvedTask, TaskOutcome?>? Immediate { get; init; }

        public async Task<TaskOutcome> RunAsync(ResolvedTask task, IProgress<TaskUsage> progress, CancellationToken ct)
        {
            TaskCompletionSource<TaskOutcome> tcs;
            lock (_lock)
            {
                Started.Add(task.Id);
                Tasks[task.Id] = task;
                Running++;
                MaxRunning = Math.Max(MaxRunning, Running);
                tcs = Pending[task.Id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Progress[task.Id] = progress;
                Tokens[task.Id] = ct;
            }
            try
            {
                if (Immediate?.Invoke(task) is { } now)
                    return now;
                await using var _ = ct.Register(() => tcs.TrySetResult(new TaskOutcome(task.Id, TaskState.Cancelled, $"run-{task.Id}", "stopped")));
                return await tcs.Task;
            }
            finally
            {
                lock (_lock)
                    Running--;
            }
        }

        public void Finish(string id, TaskState state = TaskState.Completed, long tokens = 100, decimal usd = 0, ErrorCode? error = null) =>
            PendingOf(id).TrySetResult(new TaskOutcome(id, state, $"run-{id}", null, error, usd, tokens, state == TaskState.Completed ? $"chargehand/run-{id}" : null,
                state == TaskState.Completed ? $"https://example.test/pull/{id}" : null));

        private TaskCompletionSource<TaskOutcome> PendingOf(string id)
        {
            lock (_lock)
                return Pending[id];
        }

        // Under the lock: a start adds to Started and to Pending in one step, and an unlocked count can see the first without the second.
        private int StartedCount()
        {
            lock (_lock)
                return Started.Count;
        }

        public async Task WaitStarted(int count)
        {
            for (var i = 0; i < 200 && StartedCount() < count; i++)
                await Task.Delay(25);
            Assert.True(StartedCount() >= count, $"expected {count} started, saw {StartedCount()}");
        }
    }

    [Fact]
    public async Task Never_more_than_max_parallel_run_at_once_and_tasks_start_in_order()
    {
        var runner = new ScriptedRunner();
        var batch = new BatchScheduler(runner).RunAsync(Tasks(5), Limits(parallel: 2), default);
        await runner.WaitStarted(2);
        await Task.Delay(150);
        Assert.Equal(["t1", "t2"], runner.Started);
        runner.Finish("t1");
        await runner.WaitStarted(3);
        runner.Finish("t2");
        runner.Finish("t3");
        await runner.WaitStarted(5);
        runner.Finish("t4");
        runner.Finish("t5");
        var outcome = await batch;
        Assert.True(runner.MaxRunning <= 2);
        Assert.Equal(["t1", "t2", "t3", "t4", "t5"], runner.Started);
        Assert.Equal(["t1", "t2", "t3", "t4", "t5"], outcome.Tasks.Select(t => t.Id));
        Assert.All(outcome.Tasks, t => Assert.Equal(TaskState.Completed, t.State));
        Assert.All(outcome.Tasks, t => Assert.Equal($"run-{t.Id}", t.RunId));
        Assert.True(outcome.AllCompleted);
        Assert.Equal(500, outcome.Tokens);
    }

    [Fact]
    public async Task Batch_does_not_start_a_task_that_could_exceed_the_cap()
    {
        var runner = new ScriptedRunner();
        // Cap 5000 tokens, 2000 per task: two tasks reserve 4000, a third could reach 6000, so it waits.
        var batch = new BatchScheduler(runner).RunAsync(Tasks(3), Limits(parallel: 3, tokens: 5000, perTaskTokens: 2000), default);
        await runner.WaitStarted(2);
        runner.Progress["t1"].Report(new TaskUsage(2000, 0));
        runner.Progress["t2"].Report(new TaskUsage(2000, 0));
        await Task.Delay(200);
        Assert.Equal(["t1", "t2"], runner.Started);
        // They finish at 1500 each: 3000 spent, 2000 left, and a third task's cap is 2000: it may start now.
        runner.Finish("t1", tokens: 1500);
        runner.Finish("t2", tokens: 1500);
        await runner.WaitStarted(3);
        runner.Finish("t3", tokens: 1000);
        Assert.All((await batch).Tasks, t => Assert.Equal(TaskState.Completed, t.State));
    }

    [Fact]
    public async Task A_task_that_can_never_fit_is_not_started_and_says_why()
    {
        var runner = new ScriptedRunner();
        var batch = new BatchScheduler(runner).RunAsync(Tasks(3), Limits(parallel: 1, tokens: 2500, perTaskTokens: 2000), default);
        await runner.WaitStarted(1);
        runner.Finish("t1", tokens: 1800);          // 700 left: no further task fits its 2000 cap
        var outcome = await batch;
        Assert.Equal(TaskState.Completed, outcome.Tasks[0].State);
        Assert.Equal([TaskState.NotStarted, TaskState.NotStarted], outcome.Tasks.Skip(1).Select(t => t.State));
        Assert.All(outcome.Tasks.Skip(1), t => Assert.Contains("cap", t.Detail));
        Assert.All(outcome.Tasks.Skip(1), t => Assert.Equal(ErrorCode.CostCapReached, t.Error));
        Assert.False(outcome.AllCompleted);
    }

    [Fact]
    public async Task The_dollar_cap_binds_the_same_way()
    {
        var runner = new ScriptedRunner();
        var batch = new BatchScheduler(runner).RunAsync(Tasks(2), Limits(parallel: 2, usd: 3m, perTaskUsd: 2m), default);
        await runner.WaitStarted(1);
        await Task.Delay(150);
        Assert.Equal(["t1"], runner.Started);       // two tasks could reach 4 dollars over a cap of 3
        runner.Finish("t1", usd: 0.5m);
        await runner.WaitStarted(2);
        runner.Finish("t2", usd: 0.5m);
        Assert.Equal(1m, (await batch).Usd);
    }

    [Fact]
    public async Task Running_tasks_are_stopped_when_the_batch_is_over_by_more_than_one_task_cap()
    {
        var runner = new ScriptedRunner();
        var batch = new BatchScheduler(runner).RunAsync(Tasks(2), Limits(parallel: 2, tokens: 3000, perTaskTokens: 1000), default);
        await runner.WaitStarted(2);
        runner.Progress["t1"].Report(new TaskUsage(2500, 0));
        runner.Progress["t2"].Report(new TaskUsage(2500, 0));      // 5000 against 3000 + 1000
        var outcome = await batch;
        Assert.All(outcome.Tasks, t => Assert.Equal(ErrorCode.CostCapReached, t.Error));
        Assert.All(outcome.Tasks, t => Assert.NotEqual(TaskState.Completed, t.State));
        Assert.True(runner.Tokens["t1"].IsCancellationRequested);
    }

    [Fact]
    public async Task A_task_that_reports_more_than_its_own_cap_is_stopped_while_it_runs()
    {
        var runner = new ScriptedRunner();
        var batch = new BatchScheduler(runner).RunAsync(Tasks(2), Limits(parallel: 2, perTaskTokens: 1000), default);
        await runner.WaitStarted(2);
        runner.Progress["t1"].Report(new TaskUsage(1200, 0));
        await Task.Delay(100);
        Assert.True(runner.Tokens["t1"].IsCancellationRequested);
        Assert.False(runner.Tokens["t2"].IsCancellationRequested);
        runner.Finish("t2");
        var outcome = await batch;
        Assert.Equal(TaskState.Failed, outcome.Tasks[0].State);
        Assert.Equal(ErrorCode.CostCapReached, outcome.Tasks[0].Error);
        Assert.Equal(1200, outcome.Tasks[0].Tokens);         // what it used before it was stopped still counts
        Assert.Equal(TaskState.Completed, outcome.Tasks[1].State);
    }

    [Fact]
    public async Task A_task_over_its_dollar_cap_is_stopped_too()
    {
        var runner = new ScriptedRunner();
        var batch = new BatchScheduler(runner).RunAsync(Tasks(1), Limits(parallel: 1, perTaskUsd: 2m), default);
        await runner.WaitStarted(1);
        runner.Progress["t1"].Report(new TaskUsage(10, 2.5m));
        var outcome = await batch;
        Assert.Equal(ErrorCode.CostCapReached, outcome.Tasks.Single().Error);
        Assert.Equal(2.5m, outcome.Usd);
    }

    [Fact]
    public async Task The_batch_cap_stops_running_tasks_as_soon_as_their_reported_use_passes_it()
    {
        var runner = new ScriptedRunner();
        var batch = new BatchScheduler(runner).RunAsync(Tasks(2), Limits(parallel: 2, tokens: 3000, perTaskTokens: 1500), default);
        await runner.WaitStarted(2);
        runner.Progress["t1"].Report(new TaskUsage(1400, 0));
        runner.Progress["t2"].Report(new TaskUsage(1400, 0));      // 2800 of 3000: still inside
        await Task.Delay(100);
        Assert.False(runner.Tokens["t1"].IsCancellationRequested);
        runner.Progress["t2"].Report(new TaskUsage(1500, 0));      // 2900
        runner.Progress["t1"].Report(new TaskUsage(1500, 0));      // 3000: not over yet
        await Task.Delay(100);
        Assert.False(runner.Tokens["t1"].IsCancellationRequested);
        runner.Progress["t1"].Report(new TaskUsage(1501, 0));      // 3001
        var outcome = await batch;
        Assert.All(outcome.Tasks, t => Assert.Equal(ErrorCode.CostCapReached, t.Error));
    }

    [Fact]
    public async Task A_rate_limited_task_holds_new_starts_until_its_reset_time_and_running_tasks_go_on()
    {
        var runner = new ScriptedRunner();
        var batch = new BatchScheduler(runner, backoff: TimeSpan.FromMilliseconds(100)).RunAsync(Tasks(3), Limits(parallel: 2), default);
        await runner.WaitStarted(2);
        var resets = DateTimeOffset.UtcNow.AddMilliseconds(1200);
        runner.Pending["t1"].TrySetResult(new TaskOutcome("t1", TaskState.Failed, "run-t1", "limit", ErrorCode.RateLimited, RetryAfter: resets));
        await Task.Delay(500);
        Assert.Equal(["t1", "t2"], runner.Started);           // t3 waits for the reset, although a slot is free
        runner.Finish("t2");
        await runner.WaitStarted(3);
        Assert.True(DateTimeOffset.UtcNow >= resets);
        runner.Finish("t3");
        var outcome = await batch;
        Assert.Equal([TaskState.Failed, TaskState.Completed, TaskState.Completed], outcome.Tasks.Select(t => t.State));
        Assert.Null(outcome.Action);
    }

    [Fact]
    public async Task A_rate_limit_that_names_no_reset_holds_new_starts_for_the_fixed_backoff()
    {
        var runner = new ScriptedRunner();
        var batch = new BatchScheduler(runner, backoff: TimeSpan.FromMilliseconds(1000)).RunAsync(Tasks(2), Limits(parallel: 1), default);
        await runner.WaitStarted(1);
        var limited = DateTimeOffset.UtcNow;
        runner.Finish("t1", TaskState.Failed, error: ErrorCode.RateLimited);
        await Task.Delay(400);
        Assert.Equal(["t1"], runner.Started);
        await runner.WaitStarted(2);
        Assert.True(DateTimeOffset.UtcNow - limited >= TimeSpan.FromMilliseconds(950));
        runner.Finish("t2");
        Assert.True((await batch).Tasks[1].State == TaskState.Completed);
    }

    [Fact]
    public async Task A_reset_time_already_past_still_pauses_for_the_backoff()
    {
        var runner = new ScriptedRunner();
        var batch = new BatchScheduler(runner, backoff: TimeSpan.FromMilliseconds(900)).RunAsync(Tasks(2), Limits(parallel: 1), default);
        await runner.WaitStarted(1);
        var limited = DateTimeOffset.UtcNow;
        runner.Pending["t1"].TrySetResult(new TaskOutcome("t1", TaskState.Failed, "run-t1", "limit", ErrorCode.RateLimited, RetryAfter: limited.AddHours(-1)));
        await Task.Delay(400);
        Assert.Equal(["t1"], runner.Started);
        await runner.WaitStarted(2);
        Assert.True(DateTimeOffset.UtcNow - limited >= TimeSpan.FromMilliseconds(850));
        runner.Finish("t2");
        await batch;
    }

    [Fact]
    public async Task A_reset_sooner_than_the_backoff_waits_for_the_backoff()
    {
        var runner = new ScriptedRunner();
        var batch = new BatchScheduler(runner, backoff: TimeSpan.FromMilliseconds(900)).RunAsync(Tasks(2), Limits(parallel: 1), default);
        await runner.WaitStarted(1);
        var limited = DateTimeOffset.UtcNow;
        runner.Pending["t1"].TrySetResult(new TaskOutcome("t1", TaskState.Failed, "run-t1", "limit", ErrorCode.RateLimited, RetryAfter: limited.AddMilliseconds(100)));
        await runner.WaitStarted(2);
        Assert.True(DateTimeOffset.UtcNow - limited >= TimeSpan.FromMilliseconds(850));
        runner.Finish("t2");
        await batch;
    }

    [Fact]
    public async Task Two_rate_limits_in_a_row_with_no_task_completing_between_end_the_batch_rate_limited_and_the_rest_not_started()
    {
        var runner = new ScriptedRunner();
        var batch = new BatchScheduler(runner, backoff: TimeSpan.FromMilliseconds(200)).RunAsync(Tasks(4), Limits(parallel: 1), default);
        await runner.WaitStarted(1);
        runner.Finish("t1", TaskState.Failed, error: ErrorCode.RateLimited);
        await runner.WaitStarted(2);
        runner.Finish("t2", TaskState.Failed, error: ErrorCode.RateLimited);
        var outcome = await batch;
        Assert.Equal(["t1", "t2"], runner.Started);
        Assert.Equal(ErrorCode.RateLimited, outcome.Error);
        Assert.Equal([TaskState.Failed, TaskState.Failed, TaskState.NotStarted, TaskState.NotStarted], outcome.Tasks.Select(t => t.State));
        Assert.All(outcome.Tasks.Skip(2), t => Assert.Equal(ErrorCode.RateLimited, t.Error));
        Assert.All(outcome.Tasks.Skip(2), t => Assert.Contains("rate", t.Detail, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_task_that_completes_between_two_rate_limits_resets_the_count()
    {
        var runner = new ScriptedRunner();
        var batch = new BatchScheduler(runner, backoff: TimeSpan.FromMilliseconds(150)).RunAsync(Tasks(4), Limits(parallel: 1), default);
        await runner.WaitStarted(1);
        runner.Finish("t1", TaskState.Failed, error: ErrorCode.RateLimited);
        await runner.WaitStarted(2);
        runner.Finish("t2");
        await runner.WaitStarted(3);
        runner.Finish("t3", TaskState.Failed, error: ErrorCode.RateLimited);
        await runner.WaitStarted(4);
        runner.Finish("t4");
        var outcome = await batch;
        Assert.Null(outcome.Error);
        Assert.Equal([TaskState.Failed, TaskState.Completed, TaskState.Failed, TaskState.Completed], outcome.Tasks.Select(t => t.State));
    }

    [Fact]
    public async Task A_rate_limit_that_lifts_later_than_the_longest_pause_stops_the_batch_and_says_how_to_switch()
    {
        var runner = new ScriptedRunner();
        var batch = new BatchScheduler(runner).RunAsync(Tasks(2), Limits(parallel: 1), default);
        await runner.WaitStarted(1);
        runner.Pending["t1"].TrySetResult(new TaskOutcome("t1", TaskState.Failed, "run-t1", "limit", ErrorCode.RateLimited, RetryAfter: DateTimeOffset.UtcNow.AddHours(7)));
        var outcome = await batch;
        Assert.Equal(["t1"], runner.Started);
        Assert.Equal(TaskState.NotStarted, outcome.Tasks[1].State);
        Assert.Contains("api_key_secret", outcome.Action);
        Assert.Equal(ErrorCode.RateLimited, outcome.Error);
    }

    [Fact]
    public async Task A_halt_during_a_rate_limit_pause_ends_the_batch()
    {
        var runner = new ScriptedRunner();
        using var halt = new CancellationTokenSource();
        var batch = new BatchScheduler(runner, backoff: TimeSpan.FromMinutes(15)).RunAsync(Tasks(2), Limits(parallel: 1), halt.Token);
        await runner.WaitStarted(1);
        runner.Finish("t1", TaskState.Failed, error: ErrorCode.RateLimited);
        await Task.Delay(300);
        await halt.CancelAsync();
        var outcome = await batch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TaskState.NotStarted, outcome.Tasks[1].State);
        Assert.Contains("cancel", outcome.Tasks[1].Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unavailable_provider_stops_the_batch_without_fallback()
    {
        const ErrorCode code = ErrorCode.ProviderUnavailable;
        var runner = new ScriptedRunner();
        var batch = new BatchScheduler(runner).RunAsync(Tasks(4), Limits(parallel: 2), default);
        await runner.WaitStarted(2);
        runner.Finish("t1", TaskState.Failed, error: code);
        await Task.Delay(200);
        Assert.Equal(["t1", "t2"], runner.Started);           // t3 and t4 never start
        runner.Finish("t2");
        var outcome = await batch;
        Assert.Equal(TaskState.Failed, outcome.Tasks[0].State);
        Assert.Equal(TaskState.Completed, outcome.Tasks[1].State);
        Assert.All(outcome.Tasks.Skip(2), t => Assert.Equal(TaskState.NotStarted, t.State));
        Assert.All(outcome.Tasks.Skip(2), t => Assert.Contains("rate", t.Detail, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("API key", outcome.Action);
        Assert.Contains("api_key_secret", outcome.Action);
    }

    [Fact]
    public async Task A_halt_cancels_running_tasks_and_leaves_the_rest_not_started()
    {
        var runner = new ScriptedRunner();
        using var halt = new CancellationTokenSource();
        var batch = new BatchScheduler(runner).RunAsync(Tasks(4), Limits(parallel: 2), halt.Token);
        await runner.WaitStarted(2);
        await halt.CancelAsync();
        var outcome = await batch;
        Assert.Equal([TaskState.Cancelled, TaskState.Cancelled], outcome.Tasks.Take(2).Select(t => t.State));
        Assert.All(outcome.Tasks.Skip(2), t => Assert.Equal(TaskState.NotStarted, t.State));
        Assert.All(outcome.Tasks.Skip(2), t => Assert.Contains("cancel", t.Detail, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, runner.Started.Count);
    }

    [Fact]
    public async Task A_runner_that_throws_fails_that_task_only()
    {
        var runner = new ScriptedRunner { Immediate = t => t.Id == "t1" ? throw new InvalidOperationException("boom") : new TaskOutcome(t.Id, TaskState.Completed, "r") };
        var outcome = await new BatchScheduler(runner).RunAsync(Tasks(3), Limits(parallel: 1), default);
        Assert.Equal(TaskState.Failed, outcome.Tasks[0].State);
        Assert.Equal(ErrorCode.Internal, outcome.Tasks[0].Error);
        Assert.Equal([TaskState.Completed, TaskState.Completed], outcome.Tasks.Skip(1).Select(t => t.State));
    }

    [Fact]
    public async Task A_task_that_fails_does_not_stop_the_others_but_the_batch_is_not_complete()
    {
        var runner = new ScriptedRunner();
        var batch = new BatchScheduler(runner).RunAsync(Tasks(3), Limits(parallel: 1), default);
        await runner.WaitStarted(1);
        runner.Finish("t1", TaskState.Failed, error: ErrorCode.VerificationFailed);
        await runner.WaitStarted(2);
        runner.Finish("t2", TaskState.NeedsInput);
        await runner.WaitStarted(3);
        runner.Finish("t3");
        var outcome = await batch;
        Assert.Equal([TaskState.Failed, TaskState.NeedsInput, TaskState.Completed], outcome.Tasks.Select(t => t.State));
        Assert.False(outcome.AllCompleted);
        Assert.Contains("t1", outcome.Incomplete);
        Assert.Contains("t2", outcome.Incomplete);
    }

    [Fact]
    public async Task The_global_limit_is_shared_between_batches()
    {
        var global = new SemaphoreSlim(2, 2);
        var runnerA = new ScriptedRunner();
        var runnerB = new ScriptedRunner();
        var a = new BatchScheduler(runnerA).RunAsync(Tasks(2), Limits(parallel: 2, global: global), default);
        var b = new BatchScheduler(runnerB).RunAsync(Tasks(2), Limits(parallel: 2, global: global), default);
        await Task.Delay(400);
        Assert.Equal(2, runnerA.Started.Count + runnerB.Started.Count);
        foreach (var id in runnerA.Started.ToList())
            runnerA.Finish(id);
        foreach (var id in runnerB.Started.ToList())
            runnerB.Finish(id);
        await Task.Delay(400);
        foreach (var r in new[] { runnerA, runnerB })
            foreach (var id in r.Started.Where(id => !r.Pending[id].Task.IsCompleted).ToList())
                r.Finish(id);
        await Task.WhenAll(a, b);
        Assert.Equal(4, runnerA.Started.Count + runnerB.Started.Count);
        Assert.Equal(2, global.CurrentCount);
    }

    private static ResolvedTask Dep(string id, params string[] dependsOn) => new(id, null, $"goal {id}", DependsOn: dependsOn.Length == 0 ? null : dependsOn);

    [Fact]
    public async Task A_task_starts_only_once_everything_it_depends_on_completed_and_gets_their_branches()
    {
        var runner = new ScriptedRunner();
        var batch = new BatchScheduler(runner).RunAsync([Dep("t1"), Dep("t2", "t1"), Dep("t3")], Limits(parallel: 3), default);
        await runner.WaitStarted(2);
        await Task.Delay(150);
        Assert.Equal(["t1", "t3"], runner.Started);            // t2 waits although a slot is free
        Assert.Null(runner.Tasks["t1"].Upstream);

        runner.Finish("t3");
        await Task.Delay(100);
        Assert.Equal(["t1", "t3"], runner.Started);
        runner.Finish("t1");
        await runner.WaitStarted(3);
        Assert.Equal(new UpstreamWork("t1", "run-t1", "chargehand/run-t1", "https://example.test/pull/t1"), Assert.Single(runner.Tasks["t2"].Upstream!));
        runner.Finish("t2");
        Assert.All((await batch).Tasks, t => Assert.Equal(TaskState.Completed, t.State));
    }

    [Fact]
    public async Task A_task_listed_before_its_dependency_still_starts_after_it()
    {
        var runner = new ScriptedRunner();
        var batch = new BatchScheduler(runner).RunAsync([Dep("b", "a"), Dep("a")], Limits(parallel: 2), default);
        await runner.WaitStarted(1);
        await Task.Delay(100);
        Assert.Equal(["a"], runner.Started);
        runner.Finish("a");
        await runner.WaitStarted(2);
        runner.Finish("b");
        var outcome = await batch;
        Assert.Equal(["b", "a"], outcome.Tasks.Select(t => t.Id));     // the batch reports in the order it was given
        Assert.True(outcome.AllCompleted);
    }

    [Fact]
    public async Task Several_dependencies_all_have_to_complete_and_arrive_in_the_order_the_task_lists_them()
    {
        var runner = new ScriptedRunner();
        var batch = new BatchScheduler(runner).RunAsync([Dep("t1"), Dep("t2"), Dep("t3", "t2", "t1")], Limits(parallel: 3), default);
        await runner.WaitStarted(2);
        runner.Finish("t1");
        await Task.Delay(150);
        Assert.Equal(["t1", "t2"], runner.Started);
        runner.Finish("t2");
        await runner.WaitStarted(3);
        Assert.Equal(["t2", "t1"], runner.Tasks["t3"].Upstream!.Select(u => u.Id));
        runner.Finish("t3");
        Assert.True((await batch).AllCompleted);
    }

    [Theory]
    [InlineData(TaskState.Failed, "failed")]
    [InlineData(TaskState.NeedsInput, "asked a question")]
    public async Task A_dependency_that_did_not_complete_leaves_its_dependents_not_started_down_the_chain_and_they_hold_no_slot(TaskState ended, string how)
    {
        var runner = new ScriptedRunner();
        List<TaskOutcome> skipped = [];
        var batch = new BatchScheduler(runner).RunAsync([Dep("t1"), Dep("t2", "t1"), Dep("t3", "t2"), Dep("t4")], Limits(parallel: 1), default, onSkipped: skipped.Add);
        await runner.WaitStarted(1);
        runner.Finish("t1", ended);
        await runner.WaitStarted(2);
        Assert.Equal(["t1", "t4"], runner.Started);            // the one slot went to the independent task
        runner.Finish("t4");
        var outcome = await batch;

        Assert.Equal([ended, TaskState.NotStarted, TaskState.NotStarted, TaskState.Completed], outcome.Tasks.Select(t => t.State));
        Assert.Equal($"not started: the task it depends on, 't1', {how}", outcome.Tasks[1].Detail);
        Assert.Equal("not started: the task it depends on, 't2', was not started", outcome.Tasks[2].Detail);
        Assert.Equal(["t2", "t3"], skipped.Select(o => o.Id));
        Assert.Equal(["t1", "t4"], runner.Started);
    }

    [Fact]
    public async Task A_dependency_cycle_that_got_past_the_request_check_does_not_hang_the_batch()
    {
        var runner = new ScriptedRunner();
        var outcome = await new BatchScheduler(runner).RunAsync([Dep("a", "b"), Dep("b", "a")], Limits(), default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(outcome.Tasks, t => Assert.Equal(TaskState.NotStarted, t.State));
        Assert.Contains("can never complete", outcome.Tasks[0].Detail);
        Assert.Empty(runner.Started);
    }
}
