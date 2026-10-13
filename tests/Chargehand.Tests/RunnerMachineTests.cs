using System.Net.Http.Headers;
using System.Text.Json;
using Chargehand.Containers;
using Chargehand.Driven;
using Chargehand.Runner;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>ADR 0058: a runner on a laptop looks after the machine. It says <c>draining</c> while the machine is on battery, has its lid closed or has been told to stop taking work; it holds the machine awake while a session
/// runs; and it reports the memory of the engine's virtual machine, not of the laptop. The tools of macOS (<c>pmset</c>, <c>ioreg</c>, <c>caffeinate</c>) are replaced by text and fakes.</summary>
public class RunnerMachineTests
{
    private const string Image = "registry.example/session@sha256:1111111111111111111111111111111111111111111111111111111111111111";

    // --- draining

    private const string OnAc = "Now drawing from 'AC Power'\n -InternalBattery-0 (id=7274595)\t71%; charging; 1:19 remaining present: true\n";
    private const string OnBattery = "Now drawing from 'Battery Power'\n -InternalBattery-0 (id=7274595)\t71%; discharging; 4:02 remaining present: true\n";
    private const string LidOpen = "  |   \"AppleClamshellCausesSleep\" = Yes\n  |   \"Last Sleep Reason\" = \"Clamshell Sleep\"\n  |   \"AppleClamshellState\" = No\n";
    private const string LidShut = "  |   \"AppleClamshellCausesSleep\" = Yes\n  |   \"AppleClamshellState\" = Yes\n";

    private sealed class FakePower(string? battery, string? clamshell) : IPowerReader
    {
        public string? Battery { get; set; } = battery;
        public string? Clamshell { get; set; } = clamshell;
        public int Reads;

        public Task<string?> BatteryAsync(CancellationToken ct) { Interlocked.Increment(ref Reads); return Task.FromResult(Battery); }
        public Task<string?> ClamshellAsync(CancellationToken ct) { Interlocked.Increment(ref Reads); return Task.FromResult(Clamshell); }
    }

    [Theory]
    [InlineData(OnBattery, true)]
    [InlineData(OnAc, false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Battery_power_is_read_from_the_first_line_of_pmset(string? output, bool expected) => Assert.Equal(expected, HostDrain.OnBattery(output));

    [Theory]
    [InlineData(LidShut, true)]
    [InlineData(LidOpen, false)]
    [InlineData("no such key", false)]
    [InlineData(null, false)]
    public void A_closed_lid_is_read_from_ioreg(string? output, bool expected) => Assert.Equal(expected, HostDrain.LidClosed(output));

    [Fact]
    public async Task A_rule_that_is_off_never_drains_and_one_that_is_on_says_why()
    {
        var power = new FakePower(OnBattery, LidShut);
        Assert.Null(await new HostDrain(new DrainRules(), power).ReasonAsync(default));
        Assert.Equal(0, power.Reads);                                            // nothing is asked while no rule is on
        Assert.Equal("on battery power", await new HostDrain(new DrainRules(OnBattery: true), power).ReasonAsync(default));
        Assert.Equal("the lid is closed", await new HostDrain(new DrainRules(OnLidClosed: true), power).ReasonAsync(default));
        Assert.Equal("the lid is closed", await new HostDrain(new DrainRules(OnBattery: true, OnLidClosed: true), power).ReasonAsync(default));
        Assert.Null(await new HostDrain(new DrainRules(true, true), new FakePower(OnAc, LidOpen)).ReasonAsync(default));
    }

    [Fact]
    public async Task A_tool_that_answers_nothing_is_not_taken_for_a_yes()
    {
        Assert.Null(await new HostDrain(new DrainRules(true, true), new FakePower(null, null)).ReasonAsync(default));
    }

    [Fact]
    public async Task The_drain_file_drains_whatever_the_machine_says_and_ending_it_ends_the_drain()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "drain");
        var time = new ManualTimeProvider();
        var drain = new HostDrain(new DrainRules(File: file), new FakePower(OnAc, LidOpen), time);

        Assert.Null(await drain.ReasonAsync(default));
        await File.WriteAllTextAsync(file, "");
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal("the drain file drain exists", await drain.ReasonAsync(default));
        File.Delete(file);
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.Null(await drain.ReasonAsync(default));
    }

    [Fact]
    public async Task The_answer_is_kept_for_ten_seconds_so_a_server_that_asks_often_runs_the_tools_once()
    {
        var time = new ManualTimeProvider();
        var power = new FakePower(OnBattery, LidOpen);
        var drain = new HostDrain(new DrainRules(OnBattery: true), power, time);

        await drain.ReasonAsync(default);
        await drain.ReasonAsync(default);
        Assert.Equal(1, power.Reads);
        power.Battery = OnAc;
        Assert.Equal("on battery power", await drain.ReasonAsync(default));
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.Null(await drain.ReasonAsync(default));
        Assert.Equal(2, power.Reads);
    }

    [Theory]
    [InlineData("battery", true, false)]
    [InlineData("lid", false, true)]
    [InlineData("battery,lid", true, true)]
    [InlineData("lid, battery", true, true)]
    [InlineData("", false, false)]
    public void Drain_on_names_the_rules(string list, bool battery, bool lid)
    {
        var rules = DrainRules.ParseOn(list, "/tmp/drain")!;
        Assert.Equal((battery, lid, "/tmp/drain"), (rules.OnBattery, rules.OnLidClosed, rules.File));
    }

    [Fact]
    public void A_word_that_is_no_rule_is_refused() => Assert.Null(DrainRules.ParseOn("battery,sunshine", null));

    private sealed class FixedHost(HostSnapshot? snapshot) : IHostSource
    {
        public Task<HostSnapshot?> ReadAsync(CancellationToken ct) => Task.FromResult(snapshot);
    }

    [Fact]
    public async Task A_draining_host_adds_its_state_to_the_figures_and_a_ready_one_changes_nothing()
    {
        var figures = new HostSnapshot(4000, 8000, 0, 0, 0.3, 4);
        var ready = new DrainingHost(new FixedHost(figures), new HostDrain(new DrainRules(OnBattery: true), new FakePower(OnAc, LidOpen)));
        var draining = new DrainingHost(new FixedHost(figures), new HostDrain(new DrainRules(OnBattery: true), new FakePower(OnBattery, LidOpen)));
        var unknown = new DrainingHost(new FixedHost(null), new HostDrain(new DrainRules(OnBattery: true), new FakePower(OnBattery, LidOpen)));

        Assert.Equal(figures, await ready.ReadAsync(default));
        Assert.Equal(figures with { State = "draining", StateReason = "on battery power" }, await draining.ReadAsync(default));
        // A host that reported nothing still says it is draining.
        Assert.True((await unknown.ReadAsync(default))!.IsDraining);
    }

    [Fact]
    public async Task The_runner_answers_host_with_draining_only_while_it_is()
    {
        const string Key = "runner-key-for-tests";
        var power = new FakePower(OnBattery, LidOpen);
        var host = new DrainingHost(new FixedHost(new HostSnapshot(4000, 8000, 0, 0, 0.3, 4)), new HostDrain(new DrainRules(OnBattery: true), power, keepFor: TimeSpan.Zero));
        var app = RunnerServer.Create(new RunnerSettings(0, Key, new RunnerPolicy([Image], "registry.example/chargehand@sha256:2222222222222222222222222222222222222222222222222222222222222222")), new NoEngine(), host);
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);

            using var draining = JsonDocument.Parse(await http.GetStringAsync("/host"));
            Assert.Equal("draining", draining.RootElement.GetProperty("state").GetString());
            Assert.Equal("on battery power", draining.RootElement.GetProperty("state_reason").GetString());
            Assert.Equal(4000, draining.RootElement.GetProperty("available_mb").GetInt64());

            power.Battery = OnAc;
            using var ready = JsonDocument.Parse(await http.GetStringAsync("/host"));
            Assert.False(ready.RootElement.TryGetProperty("state", out _));        // a server older than the field still reads it
            Assert.False(ready.RootElement.TryGetProperty("state_reason", out _));
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private sealed class NoEngine : IContainerEngine
    {
        public Task<string> StartAsync(ContainerSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public Task SignalAsync(string id, string signal, CancellationToken ct) => throw new NotSupportedException();
        public Task<ContainerState> InspectAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task KillAllAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string> LogsTailAsync(string id, int bytes, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateVolumeAsync(string name, string runId, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveVolumeAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateNetworkAsync(string name, string batchId, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveNetworkAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> StartEgressAsync(EgressSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public Task ConnectNetworkAsync(string container, string network, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> OwnsAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<EngineResources> ListAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    // --- keep-awake

    private sealed class FakeHolder : IAwakeHolder
    {
        public bool Held { get; private set; }
        public List<string> Calls { get; } = [];

        public void Hold() { Held = true; Calls.Add("hold"); }
        public void Release() { Held = false; Calls.Add("release"); }
    }

    [Fact]
    public async Task The_machine_is_held_awake_while_a_session_runs_and_let_go_when_none_does()
    {
        var running = 0;
        var holder = new FakeHolder();
        var log = new List<string>();
        var awake = new KeepAwake(_ => Task.FromResult(running), holder, log: log.Add);

        await awake.TickAsync(default);
        Assert.Empty(holder.Calls);                           // nothing runs: nothing is held
        running = 2;
        await awake.TickAsync(default);
        await awake.TickAsync(default);
        Assert.Equal(["hold"], holder.Calls);                 // held once, however many sessions
        running = 1;
        await awake.TickAsync(default);
        Assert.True(holder.Held);
        running = 0;
        await awake.TickAsync(default);
        await awake.TickAsync(default);

        Assert.Equal(["hold", "release"], holder.Calls);
        Assert.Equal(["keep-awake: 2 session(s) running, holding the machine awake", "keep-awake: no session running, letting the machine sleep"], log);
    }

    [Fact]
    public async Task A_count_that_cannot_be_read_leaves_the_hold_as_it_is_and_says_so_once()
    {
        var failing = false;
        var holder = new FakeHolder();
        var log = new List<string>();
        var awake = new KeepAwake(_ => failing ? throw new ChargehandException(Chargehand.Contracts.ErrorCode.ContainerUnavailable, "docker ps failed") : Task.FromResult(1), holder, log: log.Add);
        await awake.TickAsync(default);

        failing = true;
        await awake.TickAsync(default);
        await awake.TickAsync(default);

        Assert.True(holder.Held);
        Assert.Single(log, l => l.Contains("could not be counted"));
    }

    [Fact]
    public async Task Stopping_the_runner_lets_the_machine_go()
    {
        var holder = new FakeHolder();
        using var stop = new CancellationTokenSource();
        var run = new KeepAwake(_ => Task.FromResult(1), holder, TimeSpan.FromMilliseconds(5)).RunAsync(stop.Token);
        for (var i = 0; i < 200 && !holder.Held; i++)
            await Task.Delay(10);
        Assert.True(holder.Held);

        await stop.CancelAsync();
        await run;

        Assert.False(holder.Held);
    }

    // --- the engine's own figures

    private const string ProbeOutput = """
        MemTotal:        3053772 kB
        MemFree:          201000 kB
        MemAvailable:    2341956 kB
        SwapTotal:       1048576 kB
        SwapFree:        1048000 kB
        #chargehand-loadavg
        0.52 0.40 0.31 2/731 12345
        #chargehand-pressure
        some avg10=1.50 avg60=0.80 avg300=0.20 total=12345
        full avg10=0.00 avg60=0.00 avg300=0.00 total=0
        #chargehand-cpus
        4
        """;

    [Fact]
    public void The_probe_output_becomes_the_figures_of_the_engines_machine()
    {
        var snapshot = EngineHostProbe.Parse(ProbeOutput);

        Assert.Equal((2287L, 2982L, 0.52, 4, 1.5), (snapshot.AvailableMb, snapshot.TotalMb, snapshot.Load1, snapshot.Cpus, snapshot.MemoryPressureAvg10));
        Assert.Equal((1024L, 1023L), (snapshot.SwapTotalMb, snapshot.SwapFreeMb));
    }

    [Fact]
    public void A_kernel_without_pressure_information_leaves_it_out()
    {
        var snapshot = EngineHostProbe.Parse(ProbeOutput.Replace("some avg10=1.50 avg60=0.80 avg300=0.20 total=12345\nfull avg10=0.00 avg60=0.00 avg300=0.00 total=0\n", "", StringComparison.Ordinal));

        Assert.Null(snapshot.MemoryPressureAvg10);
        Assert.Equal(4, snapshot.Cpus);
    }

    [Fact]
    public void The_probe_container_has_no_network_no_mount_no_name_and_nothing_to_write()
    {
        var args = ContainerTemplate.HostProbeArgs(Image);
        string After(string flag) => args[args.ToList().IndexOf(flag) + 1];

        Assert.Equal("none", After("--network"));
        Assert.Contains("--read-only", args);
        Assert.Equal("ALL", After("--cap-drop"));
        Assert.Equal("10001:10001", After("--user"));
        Assert.DoesNotContain("-v", args);
        Assert.DoesNotContain("--name", args);                  // it collides with nothing after a restart
        Assert.DoesNotContain("--label", args);                 // and does not count against the runner's container limit
        Assert.DoesNotContain("--privileged", args);
        Assert.Throws<ArgumentException>(() => ContainerTemplate.HostProbeArgs("session:latest"));
    }

    private static (string Script, string Log) FakeDocker(TempDir dir, string body)
    {
        var log = Path.Combine(dir.Path, "calls.log");
        return (dir.Write("docker", $"#!/bin/sh\nprintf '%s\\n' \"$*\" >> '{log}'\n{body}\n"), log);
    }

    [Fact]
    public async Task The_engine_is_asked_once_for_a_few_seconds_and_a_failure_is_an_error_and_not_an_empty_answer()
    {
        using var dir = new TempDir();
        var output = dir.Write("probe.txt", ProbeOutput);
        var (docker, log) = FakeDocker(dir, $"cat '{output}'");
        var time = new ManualTimeProvider();
        var probe = new EngineHostProbe(new DockerCliEngine("/bin/sh", [docker]), Image, time: time);

        var first = await probe.ReadAsync(default);
        var second = await probe.ReadAsync(default);

        Assert.Equal(2287, first!.AvailableMb);
        Assert.Same(first, second);
        Assert.Single(File.ReadAllLines(log), l => l.StartsWith("run --rm --init --network none", StringComparison.Ordinal));
        time.Advance(TimeSpan.FromSeconds(6));
        await probe.ReadAsync(default);
        Assert.Equal(2, File.ReadAllLines(log).Count(l => l.StartsWith("run ", StringComparison.Ordinal)));

        using var failing = new TempDir();
        var broken = new EngineHostProbe(new DockerCliEngine("/bin/sh", [FakeDocker(failing, "echo 'Cannot connect to the Docker daemon' >&2; exit 1").Script]), Image);
        var e = await Assert.ThrowsAsync<ChargehandException>(() => broken.ReadAsync(default));
        Assert.Contains("Cannot connect to the Docker daemon", e.Message);
    }

    [Fact]
    public async Task Only_session_containers_count_as_running_sessions()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeDocker(dir, "printf 'chargehand-run-20261013-aa11\\nchargehand-egress-run-20261013\\nchargehand-svc-db-run-20261013\\nchargehand-xfer-run-20261013-aa11\\nchargehand-prep-run-20261013-aa11\\nchargehand-run-20261013-bb22\\n'");

        var count = await new DockerCliEngine("/bin/sh", [docker]).RunningSessionsAsync(default);

        Assert.Equal(2, count);
        Assert.Contains("--filter status=running", File.ReadAllText(log));
        Assert.Contains("--filter label=chargehand.run", File.ReadAllText(log));
    }

    // --- the command line

    private const string Egress = "registry.example/chargehand@sha256:2222222222222222222222222222222222222222222222222222222222222222";

    [Fact]
    public void The_options_of_a_runner_on_a_laptop_parse()
    {
        var settings = RunnerCli.Parse(["--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", Egress, "--keep-awake", "on", "--drain-on", "battery,lid", "--drain-file", "/var/lib/chargehand/runner/drain",
            "--host-source", "engine"], key: "k", TextWriter.Null)!;

        Assert.Equal(new DrainRules(true, true, "/var/lib/chargehand/runner/drain"), settings.Options!.Drain);
        Assert.True(settings.Options.KeepAwake);
        Assert.Equal("engine", settings.Options.HostSource);
        var plain = RunnerCli.Parse(["--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", Egress], key: "k", TextWriter.Null)!;
        Assert.False(plain.Options!.KeepAwake);
        Assert.Null(plain.Options.Drain);
        Assert.Equal("auto", plain.Options.HostSource);
    }

    [Theory]
    [InlineData("--keep-awake", "yes")]
    [InlineData("--drain-on", "battery,sunshine")]
    [InlineData("--drain-file", "relative/drain")]
    [InlineData("--drain-file", "/tmp/../etc/drain")]
    [InlineData("--host-source", "kernel")]
    public void A_bad_machine_option_prints_usage(string option, string value)
    {
        var error = new StringWriter();

        Assert.Null(RunnerCli.Parse(["--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", Egress, option, value], key: "k", error));
        Assert.Contains($"{option} has an invalid value '{value}'", error.ToString());
    }
}
