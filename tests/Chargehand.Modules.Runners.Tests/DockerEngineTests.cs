using System.Diagnostics;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Runners.Api;
using Chargehand.Tests;

namespace Chargehand.Modules.Runners.Tests;

/// <summary>The Docker CLI engine: against a fake <c>docker</c> script that records its arguments, and against a real engine when one is there.</summary>
public class DockerEngineTests
{
    private const string Digest = "0000000000000000000000000000000000000000000000000000000000000000";

    private static ContainerSpec Spec() => new("run-1", $"registry.example/session@sha256:{Digest}", "work-run-1", "out-run-1", "net-1",
        new Dictionary<string, string> { ["CHARGEHAND_RUN_TOKEN"] = "canary-token-value" }, 1024, 1, 64, ["true"]);

    /// <summary>A shell script standing in for <c>docker</c>: logs each call's arguments and, for <c>--env-file</c>, the file's text.</summary>
    private static (string Path, string Log) FakeDocker(TempDir dir, string body)
    {
        var log = System.IO.Path.Combine(dir.Path, "calls.log");
        var script = dir.Write("docker", $"#!/bin/sh\nprintf '%s\\n' \"$*\" >> '{log}'\nfor a in \"$@\"; do if [ \"$prev\" = \"--env-file\" ]; then cat \"$a\" >> '{log}.env'; fi; prev=$a; done\n{body}\n");
        return (script, log);
    }

    private static DockerCliEngine Engine(string script) => new("/bin/sh", [script]);

    [Fact]
    public async Task Start_runs_the_template_and_returns_the_container_id_and_leaves_no_env_file()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeDocker(dir, "echo abc123def");
        var id = await Engine(docker).StartAsync(Spec(), default);
        Assert.Equal("abc123def", id);
        var call = File.ReadAllLines(log).Single();
        Assert.StartsWith("run --detach", call);
        Assert.DoesNotContain("canary-token-value", call);
        Assert.Contains("CHARGEHAND_RUN_TOKEN=canary-token-value", File.ReadAllText(log + ".env"));
        var envFile = call.Split(' ')[Array.IndexOf(call.Split(' '), "--env-file") + 1];
        Assert.False(File.Exists(envFile), "the env file must be removed once docker has read it");
    }

    [Fact]
    public async Task A_service_starts_through_its_template_and_its_environment_reaches_docker_in_an_env_file_only()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeDocker(dir, "echo svc123");
        var spec = new ServiceSpec("b1", "postgres", $"docker.io/library/postgres@sha256:{Digest}", "chargehand-net-b1", 5432, new Dictionary<string, string> { ["POSTGRES_PASSWORD"] = "canary-db-password" }, ["/var/lib/postgresql/data"]);
        Assert.Equal("svc123", await Engine(docker).StartServiceAsync(spec, default));
        var call = File.ReadAllLines(log).Single();
        Assert.StartsWith("run --detach --init --name chargehand-svc-postgres-b1", call);
        Assert.DoesNotContain("canary-db-password", call);
        Assert.Contains("POSTGRES_PASSWORD=canary-db-password", File.ReadAllText(log + ".env"));

        var (failing, _) = FakeDocker(dir, "echo 'pull access denied canary-db-password' >&2; exit 125");
        var e = await Assert.ThrowsAsync<ChargehandException>(() => Engine(failing).StartServiceAsync(spec, default));
        Assert.Equal(ErrorCode.ContainerUnavailable, e.Code);
        Assert.DoesNotContain("canary-db-password", e.Message);
    }

    [Fact]
    public async Task A_failing_start_is_container_unavailable_with_an_action_and_no_secret()
    {
        using var dir = new TempDir();
        var (docker, _) = FakeDocker(dir, "echo 'Cannot connect to the Docker daemon at unix:///x. canary-token-value' >&2; exit 125");
        var e = await Assert.ThrowsAsync<ChargehandException>(() => Engine(docker).StartAsync(Spec(), default));
        Assert.Equal(ErrorCode.ContainerUnavailable, e.Code);
        Assert.NotNull(e.Action);
        Assert.DoesNotContain("canary-token-value", e.Message);
        Assert.True(ChargehandException.Retryable(ErrorCode.ContainerUnavailable));
    }

    [Theory]
    [InlineData("Error response from daemon: cannot kill container: abc123: container abc123 is not running")]
    [InlineData("Error response from daemon: No such container: abc123")]
    public async Task Signalling_a_container_that_already_ended_or_is_gone_is_not_an_error(string daemonSays)
    {
        // A cancel that loses the race with the session's own exit is still a cancel: the first batch cancelled through a runner ended
        // container_unavailable because `docker kill` said "is not running".
        using var dir = new TempDir();
        var (docker, _) = FakeDocker(dir, $"echo '{daemonSays}' >&2; exit 1");
        await Engine(docker).SignalAsync("abc123", "SIGINT", default);
    }

    [Fact]
    public async Task Any_other_kill_failure_still_throws()
    {
        using var dir = new TempDir();
        var (docker, _) = FakeDocker(dir, "echo 'permission denied while trying to connect to the docker API' >&2; exit 1");
        await Assert.ThrowsAsync<ChargehandException>(() => Engine(docker).SignalAsync("abc123", "SIGINT", default));
    }

    [Fact]
    public async Task Signal_remove_and_logs_pass_validated_arguments()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeDocker(dir, "echo line1");
        var engine = Engine(docker);
        await engine.SignalAsync("abc123", "SIGINT", default);
        await engine.RemoveAsync("abc123", default);
        Assert.Equal("line1", (await engine.LogsTailAsync("abc123", 4096, default)).Trim());
        Assert.Equal(["kill --signal=SIGINT abc123", "rm -f abc123", "logs --tail 200 abc123"], File.ReadAllLines(log));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.SignalAsync("abc123", "SIGINT --all", default));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.RemoveAsync("--force", default));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.RemoveAsync("a;b", default));
    }

    [Theory]
    [InlineData("running 0 false", ContainerStatus.Running, null, false)]
    [InlineData("exited 137 true", ContainerStatus.Exited, 137, true)]
    [InlineData("exited 0 false", ContainerStatus.Exited, 0, false)]
    [InlineData("created 0 false", ContainerStatus.Created, null, false)]
    public async Task Inspect_reads_state_exit_code_and_oom(string output, ContainerStatus status, int? exit, bool oom)
    {
        using var dir = new TempDir();
        var (docker, _) = FakeDocker(dir, $"echo '{output}'");
        var state = await Engine(docker).InspectAsync("abc123", default);
        Assert.Equal(new ContainerState(status, exit, oom), state);
    }

    private const string FullId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>A fake docker that answers <c>inspect</c> with a state (running until a file named <c>exited</c> exists) and the id, and <c>stats</c> with the first line of a file named <c>stats</c>.</summary>
    private static (string Docker, string Log) MemoryDocker(TempDir dir, string stats = "285.1MiB / 3GiB")
    {
        dir.Write("stats", stats + "\n");
        return FakeDocker(dir, $"case \"$1\" in\ninspect) if [ -f '{dir.Path}/exited' ]; then echo 'exited 0 false {FullId}'; else echo 'running 0 false {FullId}'; fi ;;\nstats) head -n 1 '{dir.Path}/stats' ;;\nesac");
    }

    private static string PeakFile(TempDir dir, string driver = "systemd") => driver == "systemd"
        ? System.IO.Path.Combine(dir.Path, "cgroup", "system.slice", $"docker-{FullId}.scope", "memory.peak")
        : System.IO.Path.Combine(dir.Path, "cgroup", "docker", FullId, "memory.peak");

    private static DockerCliEngine MeasuringEngine(TempDir dir, string docker, TimeSpan? statsEvery = null) => new("/bin/sh", [docker], System.IO.Path.Combine(dir.Path, "cgroup"), statsEvery ?? TimeSpan.Zero);

    private static void WritePeak(TempDir dir, string driver, long bytes) => dir.Write(System.IO.Path.GetRelativePath(dir.Path, PeakFile(dir, driver)), $"{bytes}\n");

    [Theory]
    [InlineData("systemd")]
    [InlineData("cgroupfs")]
    public async Task A_running_container_reports_the_peak_of_its_cgroup_in_mebibytes_rounded_up_and_docker_stats_is_not_asked(string driver)
    {
        using var dir = new TempDir();
        var (docker, log) = MemoryDocker(dir);
        WritePeak(dir, driver, 302_649_344);   // 288.6 MiB
        var state = await MeasuringEngine(dir, docker).InspectAsync("chargehand-run-1", default);
        Assert.Equal(new ContainerState(ContainerStatus.Running, null, false, 289), state);
        Assert.DoesNotContain(File.ReadAllLines(log), c => c.StartsWith("stats", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_the_hosts_cgroups_the_largest_docker_stats_reading_stands_in_for_the_peak()
    {
        using var dir = new TempDir();
        var (docker, log) = MemoryDocker(dir, "285.1MiB / 3GiB");
        var engine = MeasuringEngine(dir, docker);
        Assert.Equal(286, (await engine.InspectAsync("chargehand-run-1", default)).MemoryPeakMb);
        dir.Write("stats", "1.5GiB / 3GiB\n");
        Assert.Equal(1536, (await engine.InspectAsync("chargehand-run-1", default)).MemoryPeakMb);
        dir.Write("stats", "400MiB / 3GiB\n");
        Assert.Equal(1536, (await engine.InspectAsync("chargehand-run-1", default)).MemoryPeakMb);   // a peak never goes down
        Assert.Contains("stats --no-stream --format {{.MemUsage}} chargehand-run-1", File.ReadAllLines(log));
    }

    [Fact]
    public async Task Docker_stats_is_asked_at_most_once_per_interval()
    {
        using var dir = new TempDir();
        var (docker, log) = MemoryDocker(dir);
        var engine = MeasuringEngine(dir, docker, statsEvery: TimeSpan.FromMinutes(5));
        for (var i = 0; i < 3; i++)
            Assert.Equal(286, (await engine.InspectAsync("chargehand-run-1", default)).MemoryPeakMb);
        Assert.Single(File.ReadAllLines(log), c => c.StartsWith("stats", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_peak_outlives_the_container_until_it_is_removed()
    {
        using var dir = new TempDir();
        var (docker, _) = MemoryDocker(dir);
        var engine = MeasuringEngine(dir, docker);
        WritePeak(dir, "systemd", 600L << 20);
        Assert.Equal(600, (await engine.InspectAsync("chargehand-run-1", default)).MemoryPeakMb);
        Directory.Delete(System.IO.Path.GetDirectoryName(PeakFile(dir))!, recursive: true);   // an exited container's cgroup is gone
        dir.Write("exited", "");
        var exited = await engine.InspectAsync("chargehand-run-1", default);
        Assert.Equal(new ContainerState(ContainerStatus.Exited, 0, false, 600), exited);
        await engine.RemoveAsync("chargehand-run-1", default);
        Assert.Null((await engine.InspectAsync("chargehand-run-1", default)).MemoryPeakMb);
    }

    [Fact]
    public async Task A_container_whose_memory_cannot_be_read_still_inspects()
    {
        using var dir = new TempDir();
        var (docker, _) = FakeDocker(dir, $"case \"$1\" in\ninspect) echo 'running 0 false {FullId}' ;;\nstats) echo 'Error: nope' >&2; exit 1 ;;\nesac");
        Assert.Equal(new ContainerState(ContainerStatus.Running, null, false), await MeasuringEngine(dir, docker).InspectAsync("chargehand-run-1", default));
        var (odd, _) = MemoryDocker(dir, "-- / --");
        Assert.Null((await MeasuringEngine(dir, odd).InspectAsync("chargehand-run-1", default)).MemoryPeakMb);
    }

    [Theory]
    [InlineData("285.1MiB / 3GiB", 286)]
    [InlineData("0B / 0B", 0)]
    [InlineData("1.5GiB / 4GiB", 1536)]
    [InlineData("512KiB / 1GiB", 1)]
    [InlineData("12MB / 1GB", 12)]
    [InlineData("-- / --", null)]
    [InlineData("lots / 3GiB", null)]
    [InlineData("", null)]
    public void Docker_stats_use_is_read_as_mebibytes_rounded_up(string text, int? mb) => Assert.Equal(mb, DockerCliEngine.ParseUsageMb(text));

    [Fact]
    public async Task Inspect_of_an_unknown_container_is_missing()
    {
        using var dir = new TempDir();
        var (docker, _) = FakeDocker(dir, "echo 'Error: No such object: abc123' >&2; exit 1");
        Assert.Equal(new ContainerState(ContainerStatus.Missing, null, false), await Engine(docker).InspectAsync("abc123", default));
    }

    [Fact]
    public async Task Kill_all_removes_exactly_the_labelled_containers_then_the_labelled_networks()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeDocker(dir, "case \"$1\" in ps) printf 'aaa\\nbbb\\n';; network) [ \"$2\" = ls ] && printf 'net1\\n';; esac");
        await Engine(docker).KillAllAsync(default);
        Assert.Equal(["ps -aq --filter label=chargehand.run", "rm -f aaa bbb", "network ls -q --filter label=chargehand.run", "network rm net1"], File.ReadAllLines(log));
    }

    [Fact]
    public async Task List_asks_for_the_labelled_containers_networks_and_volumes_by_name()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeDocker(dir, "case \"$1\" in ps) printf 'chargehand-run-a\\n';; network) printf 'chargehand-net-b\\n';; volume) printf 'chargehand-work-a\\n';; esac");
        var listed = await Engine(docker).ListAsync(default);
        Assert.Equal(["chargehand-run-a"], listed.Containers);
        Assert.Equal(["chargehand-net-b"], listed.Networks);
        Assert.Equal(["chargehand-work-a"], listed.Volumes);
        Assert.Equal(["ps -a --filter label=chargehand.run --format {{.Names}}", "network ls --filter label=chargehand.run --format {{.Name}}", "volume ls --filter label=chargehand.run --format {{.Name}}"],
            File.ReadAllLines(log));
    }

    [Fact]
    public async Task Kill_all_with_nothing_running_removes_nothing()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeDocker(dir, "true");
        await Engine(docker).KillAllAsync(default);
        Assert.Equal(["ps -aq --filter label=chargehand.run", "network ls -q --filter label=chargehand.run"], File.ReadAllLines(log));
    }

    [Fact]
    public async Task Volumes_are_created_labelled_and_removed_by_name()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeDocker(dir, "true");
        var engine = Engine(docker);
        await engine.CreateVolumeAsync("work-run-1", "run-1", default);
        await engine.RemoveVolumeAsync("work-run-1", default);
        Assert.Equal(["volume create --label chargehand.run=run-1 work-run-1", "volume rm -f work-run-1"], File.ReadAllLines(log));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.CreateVolumeAsync("/etc", "run-1", default));
    }

    [Fact]
    public async Task Networks_are_internal_labelled_and_joined_by_name()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeDocker(dir, "echo egress-id");
        var engine = Engine(docker);
        await engine.CreateNetworkAsync("chargehand-net-b1", "b1", default);
        await engine.ConnectNetworkAsync("egress-id", "bridge", default);
        await engine.RemoveNetworkAsync("chargehand-net-b1", default);
        var id = await engine.StartEgressAsync(new EgressSpec("b1", $"registry.example/c@sha256:{Digest}", "chargehand-net-b1", ["api.anthropic.com"]), default);
        Assert.Equal("egress-id", id);
        var calls = File.ReadAllLines(log);
        Assert.Equal(["network create --internal --label chargehand.run=b1 chargehand-net-b1", "network connect bridge egress-id", "network rm chargehand-net-b1"], calls.Take(3));
        Assert.StartsWith("run --detach --init --name chargehand-egress-b1", calls[3]);
        await Assert.ThrowsAsync<ArgumentException>(() => engine.CreateNetworkAsync("--internal=false", "b1", default));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.ConnectNetworkAsync("egress-id", "/x", default));
    }

    [Fact]
    public async Task Ownership_is_the_run_label_and_count_is_the_labelled_containers()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeDocker(dir, "case \"$1\" in inspect) case \"$*\" in *ours*) echo run-1;; *) echo '<no value>';; esac;; ps) printf 'a\\nb\\nc\\n';; esac");
        var engine = Engine(docker);
        Assert.True(await engine.OwnsAsync("ours-1", default));
        Assert.False(await engine.OwnsAsync("theirs-1", default));
        Assert.Equal(3, await engine.CountAsync(default));
        Assert.Contains("inspect --format {{index .Config.Labels \"chargehand.run\"}} ours-1", File.ReadAllLines(log));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.OwnsAsync("--all", default));
    }

    [DockerFact]
    public async Task On_a_real_engine_a_labelled_container_is_ours_and_an_unlabelled_one_is_not()
    {
        var image = DockerFactAttribute.Image!;
        var engine = new DockerCliEngine();
        var run = "o" + Guid.NewGuid().ToString("N")[..10];
        var stranger = "cht-stranger-" + run;
        string? ours = null;
        try
        {
            var psi = new ProcessStartInfo("docker", ["run", "-d", "--name", stranger, image, "sleep", "30"]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi)!) { await p.WaitForExitAsync(); Assert.Equal(0, p.ExitCode); }
            ours = await engine.StartAsync(new ContainerSpec(run, image, "cht-w-" + run, "cht-o-" + run, "none", new Dictionary<string, string>(), 256, 1, 64, ["sleep", "30"]), default);
            Assert.True(await engine.OwnsAsync(ours, default));
            Assert.False(await engine.OwnsAsync(stranger, default));
            Assert.True(await engine.CountAsync(default) >= 1);
        }
        finally
        {
            if (ours is not null)
                await engine.RemoveAsync(ours, default);
            using var rm = Process.Start(new ProcessStartInfo("docker", ["rm", "-f", stranger]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            await rm.WaitForExitAsync();
        }
    }

    // A real engine and an image the test may run: CHARGEHAND_TEST_IMAGE is a name@sha256:... reference of a local image with sh.
    [DockerFact]
    public async Task A_real_container_starts_is_inspected_signalled_and_removed_and_kill_all_spares_unlabelled_ones()
    {
        var image = DockerFactAttribute.Image!;
        var engine = new DockerCliEngine();
        var run = "t" + Guid.NewGuid().ToString("N")[..10];
        var spec = new ContainerSpec(run, image, "cht-work-" + run, "cht-out-" + run, "none", new Dictionary<string, string>(), 256, 1, 64, ["sh", "-c", "sleep 60"]);
        string? id = null;
        try
        {
            await engine.CreateVolumeAsync(spec.WorkVolume, run, default);
            await engine.CreateVolumeAsync(spec.OutVolume, run, default);
            id = await engine.StartAsync(spec, default);
            Assert.Equal(ContainerStatus.Running, (await engine.InspectAsync(id, default)).Status);
            await engine.SignalAsync(id, "SIGKILL", default);
            for (var i = 0; i < 20 && (await engine.InspectAsync(id, default)).Status == ContainerStatus.Running; i++)
                await Task.Delay(250);
            var state = await engine.InspectAsync(id, default);
            Assert.Equal(ContainerStatus.Exited, state.Status);
            Assert.Equal(137, state.ExitCode);
            await engine.KillAllAsync(default);
            Assert.Equal(ContainerStatus.Missing, (await engine.InspectAsync(id, default)).Status);
        }
        finally
        {
            if (id is not null)
                await engine.RemoveAsync(id, default);
            await engine.RemoveVolumeAsync(spec.WorkVolume, default);
            await engine.RemoveVolumeAsync(spec.OutVolume, default);
        }
    }
}
