using System.Diagnostics;
using Chargehand.Containers;
using Chargehand.Contracts;

namespace Chargehand.Tests;

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
    public async Task Inspect_reads_state_exit_code_and_oom(string output, ContainerStatus status, int? exit, bool oom)
    {
        using var dir = new TempDir();
        var (docker, _) = FakeDocker(dir, $"echo '{output}'");
        var state = await Engine(docker).InspectAsync("abc123", default);
        Assert.Equal(new ContainerState(status, exit, oom), state);
    }

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

/// <summary>Runs a test only when a Docker engine answers and CHARGEHAND_TEST_IMAGE names a local image by digest; else skipped, with the reason.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public static string? Image { get; } = Environment.GetEnvironmentVariable("CHARGEHAND_TEST_IMAGE");

    public DockerFactAttribute() => Skip = Reason(egress: false);

    /// <summary>Why a Docker test cannot run here, or null when it can.</summary>
    internal static string? Reason(bool egress)
    {
        if (string.IsNullOrEmpty(Image))
            return "set CHARGEHAND_TEST_IMAGE to a local image reference (name@sha256:...) to run Docker tests";
        if (egress && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CHARGEHAND_TEST_EGRESS_IMAGE")))
            return "set CHARGEHAND_TEST_EGRESS_IMAGE (scripts/egress-test-image.sh <aspnet-base-image>) to run the egress tests";
        return EngineAnswers() ? null : "no Docker engine answered `docker info`";
    }

    private static bool EngineAnswers()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("docker", "info") { RedirectStandardOutput = true, RedirectStandardError = true })!;
            return p.WaitForExit(15_000) && p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>A <see cref="DockerFactAttribute"/> test that also needs the egress test image.</summary>
public sealed class DockerEgressFactAttribute : FactAttribute
{
    public DockerEgressFactAttribute() => Skip = DockerFactAttribute.Reason(egress: true);
}
