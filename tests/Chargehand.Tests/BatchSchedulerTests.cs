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
        public int Running;
        public int MaxRunning;
        public Func<ResolvedTask, TaskOutcome?>? Immediate { get; init; }

        public async Task<TaskOutcome> RunAsync(ResolvedTask task, IProgress<TaskUsage> progress, CancellationToken ct)
        {
            TaskCompletionSource<TaskOutcome> tcs;
            lock (_lock)
            {
                Started.Add(task.Id);
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
            Pending[id].TrySetResult(new TaskOutcome(id, state, $"run-{id}", null, error, usd, tokens, state == TaskState.Completed ? $"chargehand/run-{id}" : null,
                state == TaskState.Completed ? $"https://example.test/pull/{id}" : null));

        public async Task WaitStarted(int count)
        {
            for (var i = 0; i < 200 && Started.Count < count; i++)
                await Task.Delay(25);
            Assert.True(Started.Count >= count, $"expected {count} started, saw {Started.Count}");
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

    [Theory]
    [InlineData(ErrorCode.RateLimited)]
    [InlineData(ErrorCode.ProviderUnavailable)]
    public async Task Rate_limited_subscription_stops_the_batch_without_fallback(ErrorCode code)
    {
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
}
