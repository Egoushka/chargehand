using Chargehand.Containers;
using Chargehand.Contracts;

namespace Chargehand.Tests;

/// <summary>ADR 0039: the helper that moves one fixed-name file into or out of a session's output volume, and what it can never hold.</summary>
public class OutVolumeEngineTests
{
    private const string Image = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private static OutFileSpec Spec(string name) => new("run1", Image, "chargehand-out-run1", name);

    private static (string Script, string Log) FakeDocker(TempDir dir, string body)
    {
        var log = System.IO.Path.Combine(dir.Path, "calls.log");
        return (dir.Write("docker", $"#!/bin/sh\nprintf '%s\\n' \"$*\" >> '{log}'\n{body}\n"), log);
    }

    [Fact]
    public void The_helper_has_no_network_a_read_only_root_and_only_the_one_volume()
    {
        foreach (var write in new[] { true, false })
        {
            var args = ContainerTemplate.OutFileArgs(Spec(write ? "task.json" : "chargehand.bundle"), write);
            string After(string flag) => args[args.ToList().IndexOf(flag) + 1];
            Assert.Equal("none", After("--network"));
            Assert.Contains("--read-only", args);
            Assert.Equal("ALL", After("--cap-drop"));
            Assert.Equal("no-new-privileges", After("--security-opt"));
            Assert.Equal("10001:10001", After("--user"));
            Assert.Equal(write ? "chargehand-out-run1:/out" : "chargehand-out-run1:/out:ro", After("-v"));
            Assert.Single(args, a => a == "-v");
            Assert.DoesNotContain("--privileged", args);
            Assert.Equal("transfer", args[^2]);
        }
    }

    [Theory]
    [InlineData("stream.jsonl", false)]        // the log stays in the volume
    [InlineData("../etc/passwd", false)]
    [InlineData("task.json", false)]           // a result file is not task.json
    [InlineData("chargehand.bundle", true)]    // and the task file is the only one written
    [InlineData("driven-report.json", true)]
    [InlineData("session-usage.json", true)]   // the tally is read, never written, by chargehand
    public void Only_the_fixed_names_move_in_their_own_direction(string name, bool write) =>
        Assert.Throws<ArgumentException>(() => ContainerTemplate.OutFileArgs(Spec(name), write));

    [Fact]
    public async Task Write_pipes_the_bytes_to_the_helper_and_read_copies_its_output()
    {
        using var dir = new TempDir();
        var stdin = System.IO.Path.Combine(dir.Path, "stdin");
        var (docker, log) = FakeDocker(dir, $"case \"$*\" in *'/out:ro'*) printf 'bundle-bytes';; *) cat > '{stdin}';; esac");
        var engine = new DockerCliEngine("/bin/sh", [docker]);
        await engine.WriteOutFileAsync(Spec("task.json"), "{\"goal\":\"x\"}"u8.ToArray(), default);
        Assert.Equal("{\"goal\":\"x\"}", File.ReadAllText(stdin));
        using var read = new MemoryStream();
        Assert.True(await engine.ReadOutFileAsync(Spec("chargehand.bundle"), read, default));
        Assert.Equal("bundle-bytes", System.Text.Encoding.UTF8.GetString(read.ToArray()));
        Assert.Equal(2, File.ReadAllLines(log).Count(c => c.StartsWith("run --rm --init -i --name chargehand-xfer-run1", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_missing_file_reads_as_false_and_any_other_failure_is_container_unavailable()
    {
        using var dir = new TempDir();
        var engine = new DockerCliEngine("/bin/sh", [FakeDocker(dir, "exit 3").Script]);
        using var read = new MemoryStream();
        Assert.False(await engine.ReadOutFileAsync(Spec("chargehand.bundle"), read, default));
        Assert.Equal(0, read.Length);

        using var dir2 = new TempDir();
        var failing = new DockerCliEngine("/bin/sh", [FakeDocker(dir2, "echo 'no such volume' >&2; exit 125").Script]);
        var e = await Assert.ThrowsAsync<ChargehandException>(() => failing.ReadOutFileAsync(Spec("chargehand.bundle"), read, default));
        Assert.Equal(ErrorCode.ContainerUnavailable, e.Code);
        Assert.Contains("no such volume", e.Message);
        await Assert.ThrowsAsync<ChargehandException>(() => failing.WriteOutFileAsync(Spec("task.json"), new byte[] { 1 }, default));
    }

    [DockerFact]
    public async Task On_a_real_engine_a_missing_file_reads_as_false()
    {
        var run = "o" + Guid.NewGuid().ToString("N")[..8];
        var volume = "chargehand-out-" + run;
        var engine = new DockerCliEngine();
        try
        {
            await engine.CreateVolumeAsync(volume, run, default);
            using var read = new MemoryStream();
            Assert.False(await engine.ReadOutFileAsync(new OutFileSpec(run, DockerFactAttribute.Image!, volume, "chargehand.bundle"), read, default));
        }
        finally
        {
            await engine.RemoveVolumeAsync(volume, default);
        }
    }
}
