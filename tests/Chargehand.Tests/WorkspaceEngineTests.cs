using System.Diagnostics;
using Chargehand.Containers;
using Chargehand.Contracts;

namespace Chargehand.Tests;

public class WorkspaceEngineTests
{
    private const string Image = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private static WorkspaceSpec Spec(string source) => new("run1", Image, source, "chargehand-work-run1", "chargehand/run1", "abc1234");

    private static (string Script, string Log) FakeDocker(TempDir dir, string body)
    {
        var log = System.IO.Path.Combine(dir.Path, "calls.log");
        return (dir.Write("docker", $"#!/bin/sh\nprintf '%s\\n' \"$*\" >> '{log}'\n{body}\n"), log);
    }

    [Fact]
    public async Task Prepare_creates_the_volume_runs_the_helper_to_the_end_and_removes_it()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeDocker(dir, "true");
        await new DockerCliEngine("/bin/sh", [docker]).PrepareWorkspaceAsync(Spec("/srv/checkouts/r"), default);
        var calls = File.ReadAllLines(log);
        Assert.Equal("volume create --label chargehand.run=run1 chargehand-work-run1", calls[0]);
        Assert.StartsWith("run --rm --init --name chargehand-prep-run1", calls[1]);
        Assert.Contains("-v /srv/checkouts/r:/src:ro", calls[1]);
        Assert.DoesNotContain(calls, c => c.StartsWith("volume rm", StringComparison.Ordinal));       // a good preparation keeps the volume for the session
    }

    [Fact]
    public async Task A_failed_helper_removes_the_volume_and_says_container_unavailable_with_its_output()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeDocker(dir, "case \"$1\" in run) echo 'fatal: not a git repository' >&2; exit 128;; esac");
        var e = await Assert.ThrowsAsync<ChargehandException>(() => new DockerCliEngine("/bin/sh", [docker]).PrepareWorkspaceAsync(Spec("/srv/checkouts/r"), default));
        Assert.Equal(ErrorCode.ContainerUnavailable, e.Code);
        Assert.Contains("not a git repository", e.Message);
        Assert.Contains("volume rm -f chargehand-work-run1", File.ReadAllLines(log));
    }

    private static string? Image_ => Environment.GetEnvironmentVariable("CHARGEHAND_TEST_SESSION_IMAGE");

    private sealed class SessionImageFactAttribute : FactAttribute
    {
        public SessionImageFactAttribute() => Skip = string.IsNullOrEmpty(Image_)
            ? "set CHARGEHAND_TEST_SESSION_IMAGE to a locally built session image"
            : DockerFactAttribute.Reason(egress: false) is { } r && r.StartsWith("no Docker", StringComparison.Ordinal) ? r : null;
    }

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return output;
    }

    [SessionImageFact]
    public async Task On_a_real_engine_the_volume_holds_a_clone_on_the_new_branch_at_the_commit_and_the_source_is_untouched()
    {
        using var dir = new TempDir();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(dir.Path, "src")).FullName;
        Git(source, "init", "-q", "-b", "main");
        File.WriteAllText(System.IO.Path.Combine(source, "a.txt"), "a");
        Git(source, "add", "-A");
        Git(source, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "-m", "base");
        var commit = Git(source, "rev-parse", "HEAD").Trim();
        var run = "w" + Guid.NewGuid().ToString("N")[..8];
        var engine = new DockerCliEngine();
        var volume = "chargehand-work-" + run;
        try
        {
            await engine.PrepareWorkspaceAsync(new WorkspaceSpec(run, Image_!, source, volume, $"chargehand/{run}", commit), default);
            string InVolume(params string[] git)
            {
                var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "run", "--rm", "-v", $"{volume}:/work", "--entrypoint", "git", Image_! }.Concat(["-C", "/work", "-c", "safe.directory=*"]).Concat(git))
                    psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                return output;
            }
            Assert.Equal($"chargehand/{run}", InVolume("branch", "--show-current").Trim());
            Assert.Equal(commit, InVolume("rev-parse", "HEAD").Trim());
            Assert.Contains("chargehand", InVolume("config", "user.name"));
            Assert.Contains("/dev/null", InVolume("config", "core.hooksPath"));
            Assert.Equal("", Git(source, "status", "--porcelain").Trim());       // the source was mounted read-only and is unchanged
            Assert.Equal(["main"], Git(source, "branch", "--format=%(refname:short)").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        finally
        {
            await engine.RemoveVolumeAsync(volume, default);
        }
    }
}
