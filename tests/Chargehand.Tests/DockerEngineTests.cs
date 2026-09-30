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
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (script, log);
    }

    [Fact]
    public async Task Start_runs_the_template_and_returns_the_container_id_and_leaves_no_env_file()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeDocker(dir, "echo abc123def");
        var id = await new DockerCliEngine(docker).StartAsync(Spec(), default);
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
        var e = await Assert.ThrowsAsync<ChargehandException>(() => new DockerCliEngine(docker).StartAsync(Spec(), default));
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
        var engine = new DockerCliEngine(docker);
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
        var state = await new DockerCliEngine(docker).InspectAsync("abc123", default);
        Assert.Equal(new ContainerState(status, exit, oom), state);
    }

    [Fact]
    public async Task Inspect_of_an_unknown_container_is_missing()
    {
        using var dir = new TempDir();
        var (docker, _) = FakeDocker(dir, "echo 'Error: No such object: abc123' >&2; exit 1");
        Assert.Equal(new ContainerState(ContainerStatus.Missing, null, false), await new DockerCliEngine(docker).InspectAsync("abc123", default));
    }

    [Fact]
    public async Task Kill_all_removes_exactly_the_labelled_containers()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeDocker(dir, "case \"$1\" in ps) printf 'aaa\\nbbb\\n';; esac");
        await new DockerCliEngine(docker).KillAllAsync(default);
        Assert.Equal(["ps -aq --filter label=chargehand.run", "rm -f aaa bbb"], File.ReadAllLines(log));
    }

    [Fact]
    public async Task Kill_all_with_nothing_running_removes_nothing()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeDocker(dir, "true");
        await new DockerCliEngine(docker).KillAllAsync(default);
        Assert.Equal(["ps -aq --filter label=chargehand.run"], File.ReadAllLines(log));
    }

    [Fact]
    public async Task Volumes_are_created_labelled_and_removed_by_name()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeDocker(dir, "true");
        var engine = new DockerCliEngine(docker);
        await engine.CreateVolumeAsync("work-run-1", "run-1", default);
        await engine.RemoveVolumeAsync("work-run-1", default);
        Assert.Equal(["volume create --label chargehand.run=run-1 work-run-1", "volume rm -f work-run-1"], File.ReadAllLines(log));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.CreateVolumeAsync("/etc", "run-1", default));
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

    public DockerFactAttribute()
    {
        if (string.IsNullOrEmpty(Image))
            Skip = "set CHARGEHAND_TEST_IMAGE to a local image reference (name@sha256:...) to run Docker tests";
        else if (!EngineAnswers())
            Skip = "no Docker engine answered `docker info`";
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
