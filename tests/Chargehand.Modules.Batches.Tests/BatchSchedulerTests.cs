using Chargehand.Contracts;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;
using Chargehand.Tests;

namespace Chargehand.Modules.Batches.Tests;

/// <summary>ADR 0039, the shell around <c>BatchMachine</c>: tasks start in order on a runner, caps cancel what runs, a pause waits on the clock, a halt stops it, and the shared slots
/// are shared. The decisions themselves are in <see cref="BatchMachineTests"/>.</summary>
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

    // The rate-limit tests run on a clock that moves only when they say so: a pause lifts exactly when they pass it, never because a loaded machine was slow.
    private static async Task Waiting(ManualTimeProvider clock)
    {
        for (var i = 0; i < 200 && clock.Waiting == 0; i++)
            await Task.Delay(25);
        Assert.True(clock.Waiting > 0, "the batch never waited for the clock");
    }

    [Fact]
    public async Task A_rate_limited_task_holds_new_starts_until_its_reset_time_and_running_tasks_go_on()
    {
        var clock = new ManualTimeProvider();
        var runner = new ScriptedRunner();
        var paused = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        var batch = new BatchScheduler(runner, backoff: TimeSpan.FromMinutes(1), time: clock)
            .RunAsync(Tasks(3), Limits(parallel: 2), default, onPause: (_, lifts) => paused.TrySetResult(lifts));
        await runner.WaitStarted(2);
        var resets = clock.GetUtcNow().AddMinutes(20);
        runner.Pending["t1"].TrySetResult(new TaskOutcome("t1", TaskState.Failed, "run-t1", "limit", ErrorCode.RateLimited, RetryAfter: resets));
        Assert.Equal(resets, await paused.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        runner.Finish("t2");                                    // t2 ran on through the pause
        await Waiting(clock);
        clock.Advance(TimeSpan.FromMinutes(20) - TimeSpan.FromSeconds(1));
        Assert.Equal(["t1", "t2"], runner.Started);             // t3 waits for the reset, although a slot is free
        clock.Advance(TimeSpan.FromSeconds(1));
        await runner.WaitStarted(3);
        runner.Finish("t3");
        var outcome = await batch;
        Assert.Equal([TaskState.Failed, TaskState.Completed, TaskState.Completed], outcome.Tasks.Select(t => t.State));
        Assert.Null(outcome.Action);
    }

    [Fact]
    public async Task A_halt_during_a_rate_limit_pause_ends_the_batch()
    {
        var clock = new ManualTimeProvider();
        var runner = new ScriptedRunner();
        using var halt = new CancellationTokenSource();
        var batch = new BatchScheduler(runner, backoff: TimeSpan.FromMinutes(15), time: clock).RunAsync(Tasks(2), Limits(parallel: 1), halt.Token);
        await runner.WaitStarted(1);
        runner.Finish("t1", TaskState.Failed, error: ErrorCode.RateLimited);
        await Waiting(clock);
        await halt.CancelAsync();
        var outcome = await batch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TaskState.NotStarted, outcome.Tasks[1].State);
        Assert.Contains("cancel", outcome.Tasks[1].Detail, StringComparison.OrdinalIgnoreCase);
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

    [Theory]
    [InlineData(TaskState.Failed, "failed")]
    [InlineData(TaskState.NeedsInput, "asked a question")]
    public async Task A_dependency_that_did_not_complete_leaves_its_dependents_not_started_down_the_chain_and_they_hold_no_slot(TaskState ended, string how)
    {
        var runner = new ScriptedRunner();
        List<TaskOutcome> skipped = [];
        var batch = new BatchScheduler(runner).RunAsync([Dep("t1"), Dep("t2", "t1"), Dep("t3", "t2"), Dep("t4")], Limits(parallel: 1), default, onSkipped: o => { skipped.Add(o); return Task.CompletedTask; });
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
}
