using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0045, admission: a task starts only when the host has memory for its request and is not under memory pressure; a host that cannot say changes nothing.</summary>
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
    public void The_probe_reads_the_ten_second_average_of_the_some_line_as_memory_pressure()
    {
        const string Psi = "some avg10=12.34 avg60=3.10 avg300=0.50 total=987654\nfull avg10=4.00 avg60=1.00 avg300=0.10 total=12345\n";
        Assert.Equal(12.34, HostProbe.Parse(MemInfo, "0.52 0.40 0.31 2/731 12345\n", 8, Psi).MemoryPressureAvg10);
        Assert.Equal(0, HostProbe.Parse(null, null, null, "some avg10=0.00 avg60=0.00 avg300=0.00 total=0\n").MemoryPressureAvg10);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("full avg10=4.00 avg60=1.00 avg300=0.10 total=12345\n")]   // only the some line counts
    [InlineData("some avg60=3.10 total=1\n")]
    [InlineData("some avg10=lots\n")]
    public void Pressure_is_null_where_the_host_has_no_usable_pressure_text(string? text) =>
        Assert.Null(HostProbe.Parse(MemInfo, "0.52 0.40 0.31 2/731 12345\n", 8, text).MemoryPressureAvg10);

    [Fact]
    public async Task A_file_that_is_missing_or_unreadable_leaves_its_fields_null()
    {
        Assert.Equal(new HostSnapshot(null, null, null, null, null, 4), HostProbe.Parse(null, null, 4));
        Assert.Equal(new HostSnapshot(null, null, null, null, null, null), HostProbe.Parse("garbage\nMemAvailable: lots kB", "", null));
        var snapshot = (await new HostProbe("/nonexistent/meminfo", "/nonexistent/loadavg", "/nonexistent/pressure").ReadAsync(default))!;
        Assert.Null(snapshot.AvailableMb);
        Assert.Null(snapshot.Load1);
        Assert.Null(snapshot.MemoryPressureAvg10);
    }

    [Fact]
    public async Task The_probe_reads_the_three_files_it_is_given()
    {
        using var dir = new TempDir();
        var probe = new HostProbe(dir.Write("meminfo", MemInfo), dir.Write("loadavg", "0.52 0.40 0.31 2/731 12345\n"), dir.Write("pressure", "some avg10=7.50 avg60=1.00 avg300=0.10 total=1\n"));
        var snapshot = (await probe.ReadAsync(default))!;
        Assert.Equal((2355L, 0.52, 7.5), (snapshot.AvailableMb, snapshot.Load1, snapshot.MemoryPressureAvg10));
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

    private static HostSnapshot Free(long availableMb, double? load = null, int cpus = 4, double? pressure = null) => new(availableMb, 16000, 0, 0, load, cpus, pressure);

    private static HostAdmission Admission(FakeHost host, int reserveMb = 1024, double? maxLoad = null, ManualTimeProvider? time = null, List<string>? log = null, double maxPressure = 10) =>
        new(host, new DrivenAdmission(reserveMb, maxLoad, MaxMemoryPressure: maxPressure), TimeSpan.FromMilliseconds(20), time, log is null ? null : log.Add);

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
        Assert.Contains($"{4096 + HostAdmission.StartMarginMb} just started", second.Detail);
        time.Advance(HostAdmission.Settle + TimeSpan.FromSeconds(1));
        host.Snapshot = Free(6000 - 4096 + 1024 + 4096);
        Assert.True((await admission.TryAdmitAsync(4096, default)).Admit);
    }

    [Fact]
    public async Task A_container_started_a_moment_ago_counts_at_its_request_plus_a_margin_and_not_at_its_limit()
    {
        var time = new ManualTimeProvider();
        var host = new FakeHost(Free(5000));
        using var admission = Admission(host, time: time);
        const int Request = 1536;   // the container's limit may be 4096; the host is asked for the request only
        Assert.True((await admission.TryAdmitAsync(Request, default)).Admit);
        Assert.True((await admission.TryAdmitAsync(Request, default)).Admit);   // 5000 - 1024 reserved - 1664 just started = 2312, enough for 1536
        var third = await admission.TryAdmitAsync(Request, default);            // 5000 - 1024 - 2 x 1664 = 648
        Assert.False(third.Admit);
        Assert.Contains($"{2 * (Request + HostAdmission.StartMarginMb)} just started", third.Detail);
        Assert.Contains($"{Request} needed", third.Detail);
        time.Advance(HostAdmission.Settle + TimeSpan.FromSeconds(1));
        Assert.True((await admission.TryAdmitAsync(Request, default)).Admit);   // grown into their memory by now: the host's own figure counts
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
    public async Task Memory_pressure_over_its_limit_holds_new_starts_though_memory_is_free()
    {
        var host = new FakeHost(Free(9000, pressure: 14.256));
        using var admission = Admission(host);
        var idle = await admission.TryAdmitAsync(1536, default);   // nothing of this server runs: let one in
        Assert.True(idle.Admit);
        Assert.Contains("nothing else running", idle.Detail);
        var wait = await admission.TryAdmitAsync(1536, default);
        Assert.False(wait.Admit);
        Assert.Contains("waiting for memory pressure", wait.Detail);
        Assert.Contains("14.26 %", wait.Detail);
        Assert.Contains("limit 10 %", wait.Detail);
        host.Snapshot = Free(9000, pressure: 10);   // at the limit is not over it
        Assert.True((await admission.TryAdmitAsync(1536, default)).Admit);
    }

    [Fact]
    public async Task The_pressure_limit_is_the_profiles()
    {
        var host = new FakeHost(Free(9000, pressure: 40));
        using var strict = Admission(host, maxPressure: 5);
        Assert.True((await strict.TryAdmitAsync(1536, default)).Admit);
        Assert.False((await strict.TryAdmitAsync(1536, default)).Admit);
        using var off = Admission(host, maxPressure: 100);
        Assert.True((await off.TryAdmitAsync(1536, default)).Admit);
        Assert.True((await off.TryAdmitAsync(1536, default)).Admit);
    }

    [Fact]
    public async Task A_host_with_no_pressure_information_skips_the_pressure_rule()
    {
        var log = new List<string>();
        using var admission = Admission(new FakeHost(Free(9000, pressure: null)), log: log);
        for (var i = 0; i < 3; i++)
            Assert.True((await admission.TryAdmitAsync(1536, default)).Admit);
        Assert.Empty(log);   // memory is known, so this is not the host-cannot-say case
    }

    [Fact]
    public async Task Short_memory_is_reported_before_pressure_and_pressure_before_load()
    {
        var host = new FakeHost(Free(1500, load: 12, cpus: 4, pressure: 50));
        using var admission = Admission(host, maxLoad: 2);
        Assert.True((await admission.TryAdmitAsync(1536, default)).Admit);
        Assert.Contains("waiting for memory (", (await admission.TryAdmitAsync(1536, default)).Detail);
        host.Snapshot = Free(9000, load: 12, cpus: 4, pressure: 50);
        Assert.Contains("waiting for memory pressure", (await admission.TryAdmitAsync(1536, default)).Detail);
        host.Snapshot = Free(9000, load: 12, cpus: 4, pressure: 1);
        Assert.Contains("waiting for load", (await admission.TryAdmitAsync(1536, default)).Detail);
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
        var batch = new BatchScheduler(runner, admission: admission, taskRequestMb: 4096).RunAsync(Tasks(2), Limits(), default, onHold: (id, why) => holds.Add($"{id}: {why}"));
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
        var batch = new BatchScheduler(runner, admission: admission, taskRequestMb: 4096).RunAsync(Tasks(2), Limits(), default, onHold: (id, why) => holds.Add($"{id}: {why}"));
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
    public async Task The_scheduler_asks_the_host_for_the_request_not_for_a_larger_limit()
    {
        var host = new FakeHost(Free(4000));   // 4000 - 1024 = 2976 free: three requests of 900 fit, one limit of 4096 does not
        using var admission = Admission(host, time: new ManualTimeProvider());
        var runner = new Runner();
        var holds = new List<string>();
        var batch = new BatchScheduler(runner, admission: admission, taskRequestMb: 900).RunAsync(Tasks(2), Limits(), default, onHold: (id, why) => holds.Add($"{id}: {why}"));
        await runner.WaitStarted(2);
        Assert.Empty(holds);
        runner.Gates["t1"].SetResult();
        runner.Gates["t2"].SetResult();
        Assert.True((await batch).AllCompleted);
    }

    [Fact]
    public async Task The_scheduler_holds_a_task_for_memory_pressure_and_starts_it_when_pressure_falls()
    {
        var host = new FakeHost(Free(12000, pressure: 25));
        using var admission = Admission(host, time: new ManualTimeProvider());
        var runner = new Runner();
        var holds = new List<string>();
        var batch = new BatchScheduler(runner, admission: admission, taskRequestMb: 1536).RunAsync(Tasks(2), Limits(), default, onHold: (id, why) => holds.Add($"{id}: {why}"));
        await runner.WaitStarted(1);
        await Task.Delay(300);
        Assert.Equal(["t1"], runner.Started);
        Assert.Contains(holds, h => h.StartsWith("t2: waiting for memory pressure", StringComparison.Ordinal));
        host.Snapshot = Free(12000, pressure: 2);
        await runner.WaitStarted(2);
        runner.Gates["t1"].SetResult();
        runner.Gates["t2"].SetResult();
        Assert.True((await batch).AllCompleted);
    }

    [Fact]
    public async Task A_scheduler_with_an_unknown_host_starts_tasks_by_count_alone()
    {
        using var admission = Admission(new FakeHost(null));
        var runner = new Runner();
        var batch = new BatchScheduler(runner, admission: admission, taskRequestMb: 4096).RunAsync(Tasks(3), Limits(parallel: 3), default);
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
        var batch = new BatchScheduler(runner, admission: admission, taskRequestMb: 8000).RunAsync(Tasks(2), Limits(), cts.Token);
        await runner.WaitStarted(1);
        await Task.Delay(100);
        cts.Cancel();
        runner.Gates["t1"].SetResult();
        var outcome = await batch;
        Assert.Equal(TaskState.NotStarted, outcome.Tasks[1].State);
        Assert.Equal(ErrorCode.Cancelled, outcome.Tasks[1].Error);
    }
}
