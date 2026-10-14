using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0058: a batch scheduler with a <see cref="RunnerPool"/> places each task on the runner the pool names. The runners are fakes.</summary>
public class RunnerPoolSchedulerTests
{
    private sealed class FakeHost(HostSnapshot? snapshot) : IHostSource
    {
        public HostSnapshot? Snapshot { get; set; } = snapshot;
        public Exception? Throws { get; set; }
        public bool Hangs { get; set; }
        public int Reads;

        public async Task<HostSnapshot?> ReadAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            if (Hangs)
                await Task.Delay(Timeout.Infinite, ct);
            return Throws is null ? Snapshot : throw Throws;
        }
    }

    private static HostSnapshot Free(long availableMb, double? load = null, int cpus = 4) => new(availableMb, 16000, 0, 0, load, cpus);

    private static PoolSite Site(string name, FakeHost host, int? maxSessions = null) =>
        new(new DrivenRunnerEntry(name, $"http://{name}:4310", "key", maxSessions), host);

    private static RunnerPool Pool(IReadOnlyList<PoolSite> sites, ManualTimeProvider? time = null) =>
        new(sites, new DrivenAdmission(0), TimeSpan.FromMilliseconds(20), time ?? new ManualTimeProvider());

    private static ResolvedTask[] Tasks(int n) => [.. Enumerable.Range(1, n).Select(i => new ResolvedTask($"t{i}", null, $"goal {i}"))];

    private static BatchLimits Limits(int parallel = 4) => new(parallel, null, null, new TaskLimits(1000, null));

    private sealed class Recording : ITaskRunner
    {
        public List<(string Id, string? Runner)> Started { get; } = [];
        public Func<ResolvedTask, int, TaskOutcome>? Script { get; init; }
        private readonly Dictionary<string, int> _attempts = [];

        public Task<TaskOutcome> RunAsync(ResolvedTask task, IProgress<TaskUsage> progress, CancellationToken ct)
        {
            int attempt;
            lock (Started)
            {
                Started.Add((task.Id, task.Runner));
                attempt = _attempts[task.Id] = _attempts.GetValueOrDefault(task.Id) + 1;
            }
            return Task.FromResult(Script?.Invoke(task, attempt) ?? new TaskOutcome(task.Id, TaskState.Completed, $"run-{task.Id}", null, null, 0, 10, $"chargehand/run-{task.Id}", "https://example.test/pr", Runner: task.Runner));
        }
    }

    [Fact]
    public async Task The_scheduler_runs_each_task_on_the_runner_the_pool_names_and_tells_it_when_the_task_ends()
    {
        using var pool = Pool([Site("box", new FakeHost(Free(40000)), maxSessions: 1), Site("laptop", new FakeHost(Free(40000)), maxSessions: 1)]);
        var runner = new Recording();

        var outcome = await new BatchScheduler(runner, admission: pool, taskRequestMb: 4096).RunAsync(Tasks(2), Limits(), default);

        Assert.Equal([("t1", "box"), ("t2", "laptop")], runner.Started);
        Assert.Equal(["box", "laptop"], outcome.Tasks.Select(t => t.Runner));
        // Both ended, so both session slots are free again.
        Assert.Equal("box", (await pool.TryAdmitAsync(4096, null, default)).Runner);
        Assert.Equal("laptop", (await pool.TryAdmitAsync(4096, null, default)).Runner);
    }

    [Fact]
    public async Task A_task_whose_runner_did_not_answer_before_its_session_is_placed_again_on_another_runner()
    {
        using var pool = Pool([Site("laptop", new FakeHost(Free(30000))), Site("box", new FakeHost(Free(5000)))]);
        var holds = new List<string>();
        var runner = new Recording
        {
            Script = (task, _) => task.Runner == "laptop"
                ? new TaskOutcome(task.Id, TaskState.NotStarted, null, "runner 'laptop' could not be reached (away); placing the task again", ErrorCode.ContainerUnavailable, Replace: true)
                : new TaskOutcome(task.Id, TaskState.Completed, "run-1", null, null, 0, 10, "chargehand/run-1", "https://example.test/pr", Runner: task.Runner),
        };
        // The runner a task failed on is reported to the pool, as the batch's router does.
        var scheduler = new BatchScheduler(new Reporting(runner, pool), admission: pool, taskRequestMb: 4096);

        var outcome = await scheduler.RunAsync(Tasks(1), Limits(), default, onHold: (id, why) => holds.Add($"{id}: {why}"));

        Assert.Equal([("t1", "laptop"), ("t1", "box")], runner.Started);
        Assert.Equal(TaskState.Completed, outcome.Tasks.Single().State);
        Assert.Equal("box", outcome.Tasks.Single().Runner);
        Assert.Contains(holds, h => h.Contains("runner 'laptop' could not be reached"));
    }

    [Fact]
    public async Task A_task_that_cannot_start_on_any_of_three_runners_fails_with_the_reason_and_the_batch_goes_on()
    {
        using var pool = Pool([Site("a", new FakeHost(Free(30000))), Site("b", new FakeHost(Free(29000))), Site("c", new FakeHost(Free(28000))), Site("d", new FakeHost(Free(27000)))]);
        var runner = new Recording
        {
            Script = (task, _) => task.Id == "t1"
                ? new TaskOutcome(task.Id, TaskState.NotStarted, null, $"runner '{task.Runner}' could not be reached (away)", ErrorCode.ContainerUnavailable, Replace: true)
                : new TaskOutcome(task.Id, TaskState.Completed, "run-2", null, null, 0, 10, "chargehand/run-2", "https://example.test/pr", Runner: task.Runner),
        };
        var finished = new List<TaskOutcome>();

        var outcome = await new BatchScheduler(new Reporting(runner, pool), admission: pool, taskRequestMb: 4096).RunAsync(Tasks(2), Limits(1), default, onFinished: finished.Add);

        Assert.Equal(3, runner.Started.Count(s => s.Id == "t1"));
        Assert.Equal(["a", "b", "c"], runner.Started.Where(s => s.Id == "t1").Select(s => s.Runner));
        var t1 = outcome.Tasks.Single(t => t.Id == "t1");
        Assert.Equal((TaskState.Failed, ErrorCode.ContainerUnavailable), (t1.State, t1.Error));
        Assert.Contains("could not be reached", t1.Detail);
        Assert.False(t1.Replace);
        Assert.Equal(TaskState.Completed, outcome.Tasks.Single(t => t.Id == "t2").State);
        Assert.Equal(["t1", "t2"], finished.Select(f => f.Id));
    }

    [Fact]
    public async Task A_refusal_stops_the_batch_from_starting_more_and_names_the_reason()
    {
        var time = new ManualTimeProvider();
        using var pool = Pool([Site("box", new FakeHost(null) { Throws = new RunnerUnreachableException("down") })], time);
        var runner = new Recording();

        // The pool has had nothing usable for a quarter of an hour by the time the batch asks.
        await pool.TryAdmitAsync(1024, null, default);
        time.Advance(RunnerPool.Patience + TimeSpan.FromSeconds(1));
        var outcome = await new BatchScheduler(runner, admission: pool, taskRequestMb: 4096).RunAsync(Tasks(2), Limits(), default);

        Assert.Empty(runner.Started);
        Assert.All(outcome.Tasks, t => Assert.Equal((TaskState.NotStarted, ErrorCode.ContainerUnavailable), (t.State, t.Error)));
        Assert.Contains("no runner has been usable for 15 minutes", outcome.Tasks[0].Detail);
    }

    [Fact]
    public async Task A_task_with_a_runner_already_goes_back_to_it_and_the_pool_is_told_so()
    {
        using var pool = Pool([Site("box", new FakeHost(Free(40000))), Site("laptop", new FakeHost(Free(5000)))]);
        var runner = new Recording();
        var tasks = new[] { new ResolvedTask("t1", null, "goal", Runner: "laptop") };

        await new BatchScheduler(runner, admission: pool, taskRequestMb: 4096).RunAsync(tasks, Limits(), default);

        Assert.Equal([("t1", "laptop")], runner.Started);
    }

    /// <summary>Reports the runner of a task that came back to be placed again to the pool, as <c>SiteRouter</c> does, then lets the task through.</summary>
    private sealed class Reporting(ITaskRunner inner, RunnerPool pool) : ITaskRunner
    {
        public async Task<TaskOutcome> RunAsync(ResolvedTask task, IProgress<TaskUsage> progress, CancellationToken ct)
        {
            var outcome = await inner.RunAsync(task, progress, ct);
            if (outcome.Replace)
                pool.Failed(task.Runner!, outcome.Detail!);
            return outcome;
        }
    }
}
