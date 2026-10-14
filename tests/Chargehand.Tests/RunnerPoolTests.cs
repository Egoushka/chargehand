using System.Text.Json;
using Chargehand.Config;
using Chargehand.Containers;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Runners.Api;
using Chargehand.Runner;

namespace Chargehand.Tests;

/// <summary>ADR 0058: which runner a task goes to. The runners are fakes: one offline, one full, one remote, one draining, one that cannot say how much memory it has.</summary>
public class RunnerPoolTests
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

    private sealed class NoVolumes : ISessionVolumes
    {
        public Task WriteTaskAsync(string runId, SessionTask task, CancellationToken ct) => Task.CompletedTask;
        public Task WriteSeedAsync(string runId, string bundlePath, CancellationToken ct) => Task.CompletedTask;
        public Task FetchOutputAsync(string runId, string directory, CancellationToken ct) => Task.CompletedTask;
        public Task<TaskUsage?> ReadUsageAsync(string runId, CancellationToken ct) => Task.FromResult<TaskUsage?>(null);
    }

    private static HostSnapshot Free(long availableMb, double? load = null, int cpus = 4) => new(availableMb, 16000, 0, 0, load, cpus);

    private static RunnerSite Site(string name, FakeHost host, int? maxSessions = null, double weight = 1, int? reserveMb = null, string platform = "linux/amd64") =>
        new(new DrivenRunnerEntry(name, $"http://{name}:4310", "key", maxSessions, platform, weight, reserveMb), new DrivenRunTests.FakeEngine(), new DrivenRunTests.FakeEngine(), new NoVolumes(), host);

    private static RunnerPool Pool(IReadOnlyList<RunnerSite> sites, ManualTimeProvider? time = null, List<string>? log = null, int reserveMb = 0, TimeSpan? probeTimeout = null) =>
        new(sites, new DrivenAdmission(reserveMb), TimeSpan.FromMilliseconds(20), time ?? new ManualTimeProvider(), log is null ? null : log.Add, probeTimeout);

    [Fact]
    public async Task The_runner_with_the_most_free_memory_takes_the_task()
    {
        using var pool = Pool([Site("box", new FakeHost(Free(6000))), Site("laptop", new FakeHost(Free(9000)))]);

        var decision = await pool.TryAdmitAsync(4096, null, default);

        Assert.True(decision.Admit);
        Assert.Equal("laptop", decision.Runner);
        Assert.Null(decision.Detail);
    }

    [Fact]
    public async Task A_weight_scales_free_memory_and_equal_scores_go_to_the_runner_listed_first()
    {
        using (var weighted = Pool([Site("box", new FakeHost(Free(6000))), Site("laptop", new FakeHost(Free(9000)), weight: 0.5)]))
            Assert.Equal("box", (await weighted.TryAdmitAsync(4096, null, default)).Runner);
        using var tie = Pool([Site("a", new FakeHost(Free(8000))), Site("b", new FakeHost(Free(8000)))]);
        Assert.Equal("a", (await tie.TryAdmitAsync(4096, null, default)).Runner);
    }

    [Fact]
    public async Task Starts_already_made_count_against_a_runners_free_memory_so_a_burst_spreads()
    {
        using var pool = Pool([Site("box", new FakeHost(Free(9000))), Site("laptop", new FakeHost(Free(6000)))]);

        var placed = new List<string?>();
        for (var i = 0; i < 4; i++)
            placed.Add((await pool.TryAdmitAsync(4096, null, default)).Runner);

        // box 9000 and laptop 6000: box; then box shows 4904 against the laptop's 6000: laptop; then 4904 against 1904: box; then 808 and 1904: neither fits.
        Assert.Equal(["box", "laptop", "box", null], placed);
    }

    [Fact]
    public async Task A_runner_that_does_not_answer_is_skipped_and_asked_again_after_its_backoff()
    {
        var time = new ManualTimeProvider();
        var log = new List<string>();
        var offline = new FakeHost(null) { Throws = new RunnerUnreachableException("the runner could not be reached: connection refused") };
        using var pool = Pool([Site("laptop", offline), Site("box", new FakeHost(Free(5000)))], time, log);

        Assert.Equal("box", (await pool.TryAdmitAsync(4096, null, default)).Runner);
        Assert.Single(log);
        Assert.Contains("runner 'laptop' skipped for 15 s", log[0]);
        // Inside the backoff it is not asked at all.
        pool.Ended("box");
        Assert.Equal("box", (await pool.TryAdmitAsync(1024, null, default)).Runner);
        Assert.Equal(1, offline.Reads);

        // After it, it is asked, and when it answers it is used again.
        time.Advance(RunnerPool.FirstBackoff + TimeSpan.FromSeconds(1));
        offline.Throws = null;
        offline.Snapshot = Free(12000);
        pool.Ended("box");
        pool.Ended("box");
        Assert.Equal("laptop", (await pool.TryAdmitAsync(4096, null, default)).Runner);
        Assert.Contains("runner 'laptop' answers again", log);
    }

    [Fact]
    public async Task The_backoff_doubles_up_to_two_minutes()
    {
        var time = new ManualTimeProvider();
        var log = new List<string>();
        using var pool = Pool([Site("laptop", new FakeHost(null) { Throws = new RunnerUnreachableException("away") })], time, log);

        for (var i = 0; i < 7; i++)
        {
            await pool.TryAdmitAsync(1024, null, default);
            time.Advance(RunnerPool.MaxBackoff + TimeSpan.FromSeconds(1));
        }

        Assert.Equal(["15", "30", "60", "120", "120", "120", "120"], log.Select(l => l.Split("skipped for ")[1].Split(' ')[0]));
    }

    [Fact]
    public async Task A_runner_that_answers_too_slowly_counts_as_unreachable()
    {
        using var pool = Pool([Site("laptop", new FakeHost(null) { Hangs = true }), Site("box", new FakeHost(Free(5000)))], probeTimeout: TimeSpan.FromMilliseconds(50));

        var decision = await pool.TryAdmitAsync(4096, null, default);

        Assert.Equal("box", decision.Runner);
    }

    [Fact]
    public async Task A_cancel_while_asking_a_runner_is_a_cancel_and_not_an_unreachable_runner()
    {
        using var pool = Pool([Site("laptop", new FakeHost(null) { Hangs = true })]);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.TryAdmitAsync(1024, null, cancel.Token));
    }

    [Fact]
    public async Task A_host_snapshot_is_reused_for_a_few_seconds()
    {
        var time = new ManualTimeProvider();
        var host = new FakeHost(Free(20000));
        using var pool = Pool([Site("box", host)], time);

        await pool.TryAdmitAsync(1024, null, default);
        await pool.TryAdmitAsync(1024, null, default);
        Assert.Equal(1, host.Reads);
        time.Advance(RunnerPool.HealthTtl + TimeSpan.FromSeconds(1));
        await pool.TryAdmitAsync(1024, null, default);

        Assert.Equal(2, host.Reads);
    }

    [Fact]
    public async Task A_runner_at_its_session_limit_is_skipped_until_a_task_ends()
    {
        using var pool = Pool([Site("box", new FakeHost(Free(40000)), maxSessions: 1), Site("laptop", new FakeHost(Free(8000)))]);

        Assert.Equal("box", (await pool.TryAdmitAsync(1024, null, default)).Runner);
        Assert.Equal("laptop", (await pool.TryAdmitAsync(1024, null, default)).Runner);
        pool.Ended("box");

        Assert.Equal("box", (await pool.TryAdmitAsync(1024, null, default)).Runner);
    }

    [Fact]
    public async Task A_full_runner_alone_makes_the_task_wait_and_says_why()
    {
        using var pool = Pool([Site("box", new FakeHost(Free(40000)), maxSessions: 1)]);
        await pool.TryAdmitAsync(1024, null, default);

        var wait = await pool.TryAdmitAsync(1024, null, default);

        Assert.False(wait.Admit);
        Assert.False(wait.Refuse);
        Assert.Contains("box: 1 of 1 sessions running", wait.Detail);
    }

    [Fact]
    public async Task A_draining_runner_takes_no_new_task_but_still_counts_the_ones_it_has()
    {
        var time = new ManualTimeProvider();
        var host = new FakeHost(Free(40000) with { State = HostSnapshot.Draining, StateReason = "on battery" });
        using var pool = Pool([Site("laptop", host), Site("box", new FakeHost(Free(5000)))], time);

        var decision = await pool.TryAdmitAsync(4096, null, default);
        Assert.Equal("box", decision.Runner);

        // Nothing fits on the box any more, and the laptop is the bigger one: the task waits for the laptop to stop draining.
        var wait = await pool.TryAdmitAsync(4096, null, default);
        Assert.False(wait.Admit);
        Assert.Contains("laptop: draining (on battery)", wait.Detail);
        host.Snapshot = Free(40000);
        time.Advance(RunnerPool.HealthTtl + TimeSpan.FromSeconds(1));
        Assert.Equal("laptop", (await pool.TryAdmitAsync(4096, null, default)).Runner);
    }

    [Fact]
    public async Task Every_runners_memory_is_judged_on_its_own_host_with_its_own_reserve()
    {
        // 5000 free on both; the laptop keeps 4096 for its owner, so a 4096 task fits the box only.
        using var pool = Pool([Site("laptop", new FakeHost(Free(5000)), reserveMb: 4096), Site("box", new FakeHost(Free(5000)))]);

        Assert.Equal("box", (await pool.TryAdmitAsync(4096, null, default)).Runner);
    }

    [Fact]
    public async Task When_no_runner_has_room_the_task_waits_and_names_each_runner_that_was_passed_over()
    {
        using var pool = Pool([Site("box", new FakeHost(Free(4500))), Site("laptop", new FakeHost(Free(4500)))]);
        Assert.True((await pool.TryAdmitAsync(4096, null, default)).Admit);
        Assert.True((await pool.TryAdmitAsync(4096, null, default)).Admit);

        var wait = await pool.TryAdmitAsync(4096, null, default);

        Assert.False(wait.Admit);
        Assert.Contains("waiting for a runner", wait.Detail);
        Assert.Contains("box: waiting for memory", wait.Detail);
        Assert.Contains("laptop: waiting for memory", wait.Detail);
    }

    [Fact]
    public async Task With_nothing_of_this_server_running_anywhere_one_task_is_admitted_on_the_runner_with_most_free_memory()
    {
        using var pool = Pool([Site("box", new FakeHost(Free(1000))), Site("laptop", new FakeHost(Free(3000)))]);

        var first = await pool.TryAdmitAsync(4096, null, default);

        Assert.True(first.Admit);
        Assert.Equal("laptop", first.Runner);
        Assert.Contains("nothing else running", first.Detail);
        // Now something runs, so the next task waits for it rather than being forced onto a runner that has no room.
        Assert.False((await pool.TryAdmitAsync(4096, null, default)).Admit);
    }

    [Fact]
    public async Task A_task_running_on_one_runner_keeps_a_short_runner_from_being_forced()
    {
        using var pool = Pool([Site("box", new FakeHost(Free(9000))), Site("laptop", new FakeHost(Free(1000)))]);
        Assert.Equal("box", (await pool.TryAdmitAsync(4096, null, default)).Runner);
        Assert.Equal("box", (await pool.TryAdmitAsync(4096, null, default)).Runner);

        var third = await pool.TryAdmitAsync(4096, null, default);

        Assert.False(third.Admit);   // 808 left on the box, 1000 on the laptop, and the box is working: wait for it
    }

    [Fact]
    public async Task A_runner_that_cannot_say_how_much_memory_it_has_is_used_by_count_and_comes_last()
    {
        var log = new List<string>();
        using var pool = Pool([Site("old", new FakeHost(null)), Site("box", new FakeHost(Free(5000)))], log: log);

        Assert.Equal("box", (await pool.TryAdmitAsync(4096, null, default)).Runner);
        // The box is now short; the old runner has no figures, so it takes the next one.
        Assert.Equal("old", (await pool.TryAdmitAsync(4096, null, default)).Runner);
        Assert.Single(log, l => l.Contains("runner 'old' reports neither memory nor load"));
    }

    [Fact]
    public async Task A_runner_newer_than_the_server_that_sends_a_member_it_does_not_know_is_judged_by_count()
    {
        using var pool = Pool([Site("new", new FakeHost(null) { Throws = new JsonException("The JSON property 'psi' could not be mapped") })]);

        var decision = await pool.TryAdmitAsync(4096, null, default);

        Assert.Equal("new", decision.Runner);
    }

    [Fact]
    public async Task A_task_a_restart_took_up_stays_on_its_runner_without_asking_it_anything()
    {
        var offline = new FakeHost(null) { Throws = new RunnerUnreachableException("away") };
        using var pool = Pool([Site("laptop", offline, maxSessions: 1), Site("box", new FakeHost(Free(40000)))]);

        var pinned = await pool.TryAdmitAsync(4096, "laptop", default);

        Assert.True(pinned.Admit);
        Assert.Equal("laptop", pinned.Runner);
        Assert.Equal(0, offline.Reads);
        // It counts there: the laptop's one session slot is taken.
        Assert.Equal("box", (await pool.TryAdmitAsync(1024, null, default)).Runner);
    }

    [Fact]
    public async Task A_pinned_runner_the_profile_no_longer_has_is_still_named_so_the_task_runner_can_say_so()
    {
        using var pool = Pool([Site("box", new FakeHost(Free(40000)))]);

        var decision = await pool.TryAdmitAsync(4096, "gone", default);

        Assert.True(decision.Admit);
        Assert.Equal("gone", decision.Runner);
    }

    [Fact]
    public async Task With_no_runner_usable_the_task_waits_and_after_fifteen_minutes_is_refused()
    {
        var time = new ManualTimeProvider();
        using var pool = Pool([Site("box", new FakeHost(null) { Throws = new RunnerUnreachableException("down") }), Site("laptop", new FakeHost(Free(9000) with { State = "draining" }))], time);

        var wait = await pool.TryAdmitAsync(4096, null, default);
        Assert.False(wait.Admit);
        Assert.False(wait.Refuse);
        Assert.Contains("box: unreachable (down)", wait.Detail);
        Assert.Contains("laptop: draining", wait.Detail);

        time.Advance(RunnerPool.Patience - TimeSpan.FromSeconds(1));
        Assert.False((await pool.TryAdmitAsync(4096, null, default)).Refuse);
        time.Advance(TimeSpan.FromSeconds(2));
        var refused = await pool.TryAdmitAsync(4096, null, default);

        Assert.False(refused.Admit);
        Assert.True(refused.Refuse);
        Assert.Contains("no runner has been usable for 15 minutes", refused.Detail);
    }

    [Fact]
    public async Task A_runner_back_after_a_stretch_with_none_resets_the_patience()
    {
        var time = new ManualTimeProvider();
        var box = new FakeHost(null) { Throws = new RunnerUnreachableException("down") };
        using var pool = Pool([Site("box", box)], time);
        await pool.TryAdmitAsync(4096, null, default);
        time.Advance(TimeSpan.FromMinutes(14));
        box.Throws = null;
        box.Snapshot = Free(20000);
        Assert.True((await pool.TryAdmitAsync(4096, null, default)).Admit);
        pool.Ended("box");

        box.Throws = new RunnerUnreachableException("down again");
        time.Advance(TimeSpan.FromMinutes(3));
        await pool.TryAdmitAsync(4096, null, default);
        time.Advance(TimeSpan.FromMinutes(14));

        Assert.False((await pool.TryAdmitAsync(4096, null, default)).Refuse);
    }

    [Fact]
    public async Task A_task_that_could_not_start_marks_its_runner_down_for_the_next_task()
    {
        var time = new ManualTimeProvider();
        var laptop = new FakeHost(Free(30000));
        using var pool = Pool([Site("laptop", laptop), Site("box", new FakeHost(Free(5000)))], time);
        Assert.Equal("laptop", (await pool.TryAdmitAsync(4096, null, default)).Runner);

        pool.Failed("laptop", "could not be reached");
        pool.Ended("laptop");

        Assert.Equal("box", (await pool.TryAdmitAsync(4096, null, default)).Runner);
        time.Advance(RunnerPool.FirstBackoff + TimeSpan.FromSeconds(1));
        pool.Ended("box");
        Assert.Equal("laptop", (await pool.TryAdmitAsync(4096, null, default)).Runner);
    }

    [Fact]
    public async Task A_runner_that_was_away_at_the_sweep_is_swept_before_its_first_task_and_not_used_until_it_has_been()
    {
        var swept = new List<string>();
        var laptop = new FakeHost(Free(30000));
        using var pool = Pool([Site("laptop", laptop), Site("box", new FakeHost(Free(5000)))]);
        pool.Sweep = (site, _) => { swept.Add(site.Name); return Task.CompletedTask; };
        pool.MarkUnswept("laptop");

        var decision = await pool.TryAdmitAsync(4096, null, default);

        Assert.Equal("laptop", decision.Runner);
        Assert.Equal(["laptop"], swept);
        pool.Ended("laptop");
        await pool.TryAdmitAsync(4096, null, default);
        Assert.Equal(["laptop"], swept);                  // once
    }

    [Fact]
    public async Task A_sweep_that_cannot_reach_the_runner_keeps_it_out_and_is_tried_again()
    {
        var time = new ManualTimeProvider();
        var attempts = 0;
        using var pool = Pool([Site("laptop", new FakeHost(Free(30000))), Site("box", new FakeHost(Free(5000)))], time);
        pool.Sweep = (_, _) => ++attempts == 1 ? throw new RunnerUnreachableException("fell asleep") : Task.CompletedTask;
        pool.MarkUnswept("laptop");

        Assert.Equal("box", (await pool.TryAdmitAsync(4096, null, default)).Runner);
        time.Advance(RunnerPool.FirstBackoff + TimeSpan.FromSeconds(1));
        pool.Ended("box");

        Assert.Equal("laptop", (await pool.TryAdmitAsync(4096, null, default)).Runner);
        Assert.Equal(2, attempts);
    }

    // --- a batch scheduler with a pool

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
