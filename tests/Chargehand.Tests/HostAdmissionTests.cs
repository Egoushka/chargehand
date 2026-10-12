using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>ADR 0045, admission: a task starts only when the host has memory for its container; a host that cannot say changes nothing.</summary>
public class HostAdmissionTests
{
    private const string MemInfo = """
        MemTotal:       16384000 kB
        MemFree:         1000000 kB
        MemAvailable:    2411520 kB
        SwapTotal:       4194304 kB
        SwapFree:        4096000 kB
        """;

    [Fact]
    public void The_probe_reads_memory_swap_and_load_from_the_proc_text()
    {
        var snapshot = HostProbe.Parse(MemInfo, "0.52 0.40 0.31 2/731 12345\n", 8);
        Assert.Equal(new HostSnapshot(2355, 16000, 4096, 4000, 0.52, 8), snapshot);
    }

    [Fact]
    public async Task A_file_that_is_missing_or_unreadable_leaves_its_fields_null()
    {
        Assert.Equal(new HostSnapshot(null, null, null, null, null, 4), HostProbe.Parse(null, null, 4));
        Assert.Equal(new HostSnapshot(null, null, null, null, null, null), HostProbe.Parse("garbage\nMemAvailable: lots kB", "", null));
        var snapshot = (await new HostProbe("/nonexistent/meminfo", "/nonexistent/loadavg").ReadAsync(default))!;
        Assert.Null(snapshot.AvailableMb);
        Assert.Null(snapshot.Load1);
    }

    private sealed class FakeHost(HostSnapshot? snapshot) : IHostSource
    {
        public HostSnapshot? Snapshot { get; set; } = snapshot;
        public Exception? Throws { get; set; }
        public int Reads;

        public Task<HostSnapshot?> ReadAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            return Throws is null ? Task.FromResult(Snapshot) : Task.FromException<HostSnapshot?>(Throws);
        }
    }

    private static HostSnapshot Free(long availableMb, double? load = null, int cpus = 4) => new(availableMb, 16000, 0, 0, load, cpus);

    private static HostAdmission Admission(FakeHost host, int reserveMb = 1024, double? maxLoad = null, ManualTimeProvider? time = null, List<string>? log = null) =>
        new(host, new DrivenAdmission(reserveMb, maxLoad), TimeSpan.FromMilliseconds(20), time, log is null ? null : log.Add);

    [Fact]
    public async Task A_task_waits_while_available_memory_minus_the_reserve_is_below_its_limit()
    {
        var host = new FakeHost(Free(4000));
        using var admission = Admission(host);
        Assert.True((await admission.TryAdmitAsync(2048, default)).Admit);
        var wait = await admission.TryAdmitAsync(4096, default);
        Assert.False(wait.Admit);
        Assert.Contains("waiting for memory", wait.Detail);
        Assert.Contains("4000 MiB available", wait.Detail);
        host.Snapshot = Free(9000);
        Assert.True((await admission.TryAdmitAsync(4096, default)).Admit);
    }

    [Fact]
    public async Task Containers_started_a_moment_ago_still_count_against_the_free_memory()
    {
        var time = new ManualTimeProvider();
        var host = new FakeHost(Free(6000));
        using var admission = Admission(host, time: time);
        Assert.True((await admission.TryAdmitAsync(4096, default)).Admit);
        var second = await admission.TryAdmitAsync(4096, default);   // the host still shows 6000: the first has not grown yet
        Assert.False(second.Admit);
        Assert.Contains("4096 just started", second.Detail);
        time.Advance(HostAdmission.Settle + TimeSpan.FromSeconds(1));
        host.Snapshot = Free(6000 - 4096 + 1024 + 4096);
        Assert.True((await admission.TryAdmitAsync(4096, default)).Admit);
    }

    [Fact]
    public async Task With_nothing_running_one_task_is_admitted_anyway_and_the_reason_is_recorded()
    {
        using var admission = Admission(new FakeHost(Free(1500)));
        var first = await admission.TryAdmitAsync(4096, default);
        Assert.True(first.Admit);
        Assert.Contains("nothing else running", first.Detail);
        Assert.False((await admission.TryAdmitAsync(4096, default)).Admit);
        admission.Ended();
        Assert.True((await admission.TryAdmitAsync(4096, default)).Admit);
    }

    [Fact]
    public async Task A_load_over_the_limit_holds_new_starts_only_when_a_limit_is_set()
    {
        var host = new FakeHost(Free(9000, load: 12, cpus: 4));
        using (var unlimited = Admission(host))
            Assert.True((await unlimited.TryAdmitAsync(1024, default)).Admit);
        using var admission = Admission(host, maxLoad: 2);
        Assert.True((await admission.TryAdmitAsync(1024, default)).Admit);   // idle: let one in
        var wait = await admission.TryAdmitAsync(1024, default);
        Assert.False(wait.Admit);
        Assert.Contains("waiting for load", wait.Detail);
        host.Snapshot = Free(9000, load: 4, cpus: 4);
        Assert.True((await admission.TryAdmitAsync(1024, default)).Admit);
    }

    [Fact]
    public async Task A_host_that_cannot_say_admits_by_count_and_the_log_says_so_once()
    {
        var log = new List<string>();
        var host = new FakeHost(null);
        using var admission = Admission(host, log: log);
        for (var i = 0; i < 3; i++)
            Assert.True((await admission.TryAdmitAsync(4096, default)).Admit);
        host.Snapshot = new HostSnapshot(null, null, null, null, null, 4);
        Assert.True((await admission.TryAdmitAsync(4096, default)).Admit);
        host.Throws = new InvalidOperationException("boom");
        Assert.True((await admission.TryAdmitAsync(4096, default)).Admit);
        Assert.Single(log);
        Assert.Contains("does not serve /host", log[0]);
    }

    private static ResolvedTask[] Tasks(int n) => [.. Enumerable.Range(1, n).Select(i => new ResolvedTask($"t{i}", null, $"goal {i}"))];

    private static BatchLimits Limits(int parallel = 4) => new(parallel, null, null, new TaskLimits(1000, null));

    private sealed class Runner : ITaskRunner
    {
        public List<string> Started { get; } = [];
        public Dictionary<string, TaskCompletionSource> Gates { get; } = [];

        public async Task<TaskOutcome> RunAsync(ResolvedTask task, IProgress<TaskUsage> progress, CancellationToken ct)
        {
            TaskCompletionSource gate;
            lock (Started)
            {
                Started.Add(task.Id);
                gate = Gates[task.Id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            await gate.Task;
            return new TaskOutcome(task.Id, TaskState.Completed, $"run-{task.Id}", null, Tokens: 10);
        }

        public async Task WaitStarted(int count)
        {
            for (var i = 0; i < 200 && Started.Count < count; i++)
                await Task.Delay(25);
            Assert.True(Started.Count >= count, $"expected {count} started, saw {Started.Count}");
        }
    }

    [Fact]
    public async Task The_scheduler_holds_a_task_for_memory_and_starts_it_when_memory_frees()
    {
        var host = new FakeHost(Free(6000));
        using var admission = Admission(host, time: new ManualTimeProvider());
        var runner = new Runner();
        var holds = new List<string>();
        var batch = new BatchScheduler(runner, admission: admission, taskMemoryMb: 4096).RunAsync(Tasks(2), Limits(), default, onHold: (id, why) => holds.Add($"{id}: {why}"));
        await runner.WaitStarted(1);
        await Task.Delay(300);
        Assert.Equal(["t1"], runner.Started);   // t2 waits though max_parallel allows it
        Assert.Single(holds);
        Assert.StartsWith("t2: waiting for memory", holds[0]);
        host.Snapshot = Free(12000);
        await runner.WaitStarted(2);
        runner.Gates["t1"].SetResult();
        runner.Gates["t2"].SetResult();
        Assert.True((await batch).AllCompleted);
    }

    [Fact]
    public async Task The_scheduler_admits_one_task_when_none_runs_and_memory_stays_short()
    {
        var host = new FakeHost(Free(500));
        using var admission = Admission(host);
        var runner = new Runner();
        var holds = new List<string>();
        var batch = new BatchScheduler(runner, admission: admission, taskMemoryMb: 4096).RunAsync(Tasks(2), Limits(), default, onHold: (id, why) => holds.Add($"{id}: {why}"));
        await runner.WaitStarted(1);
        await Task.Delay(300);
        Assert.Equal(["t1"], runner.Started);
        Assert.Contains(holds, h => h.StartsWith("t1: admitted with nothing else running", StringComparison.Ordinal));
        runner.Gates["t1"].SetResult();
        await runner.WaitStarted(2);   // the batch does not wait forever: the next task is let in once the first has ended
        runner.Gates["t2"].SetResult();
        Assert.True((await batch).AllCompleted);
    }

    [Fact]
    public async Task A_scheduler_with_an_unknown_host_starts_tasks_by_count_alone()
    {
        using var admission = Admission(new FakeHost(null));
        var runner = new Runner();
        var batch = new BatchScheduler(runner, admission: admission, taskMemoryMb: 4096).RunAsync(Tasks(3), Limits(parallel: 3), default);
        await runner.WaitStarted(3);
        foreach (var gate in runner.Gates.Values)
            gate.SetResult();
        Assert.True((await batch).AllCompleted);
    }

    [Fact]
    public async Task A_cancelled_batch_stops_waiting_for_memory()
    {
        using var admission = Admission(new FakeHost(Free(9000)));
        var runner = new Runner();
        using var cts = new CancellationTokenSource();
        var batch = new BatchScheduler(runner, admission: admission, taskMemoryMb: 8000).RunAsync(Tasks(2), Limits(), cts.Token);
        await runner.WaitStarted(1);
        await Task.Delay(100);
        cts.Cancel();
        runner.Gates["t1"].SetResult();
        var outcome = await batch;
        Assert.Equal(TaskState.NotStarted, outcome.Tasks[1].State);
        Assert.Equal(ErrorCode.Cancelled, outcome.Tasks[1].Error);
    }
}
