using System.Diagnostics;

namespace Chargehand.Tests;

/// <summary>The session image (ADR 0039) on a real engine. Needs CHARGEHAND_TEST_SESSION_IMAGE: a locally built image
/// (<c>docker build -q -f images/session/Dockerfile .</c> prints its id).</summary>
public class SessionImageTests
{
    private static readonly string? Image = Environment.GetEnvironmentVariable("CHARGEHAND_TEST_SESSION_IMAGE");

    private sealed class SessionImageFactAttribute : FactAttribute
    {
        public SessionImageFactAttribute() => Skip = string.IsNullOrEmpty(Image)
            ? "set CHARGEHAND_TEST_SESSION_IMAGE to a locally built session image (docker build -q -f images/session/Dockerfile .)"
            : DockerFactAttribute.Reason(egress: false) is { } reason && reason.StartsWith("no Docker", StringComparison.Ordinal) ? reason : null;
    }

    private static string Run(params string[] shell)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "run", "--rm", "--entrypoint", "sh", Image!, "-c" }.Concat(shell))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return text;
    }

    [SessionImageFact]
    public void Image_has_the_toolchains_a_session_needs_and_no_docker_client()
    {
        var output = Run("dotnet --version; node --version; python3 --version; git --version; claude --version; id -u; command -v docker || echo no-docker; command -v pip3 >/dev/null && echo pip-ok");
        Assert.Matches(@"(?m)^10\.\d+\.\d+", output);           // dotnet
        Assert.Matches(@"(?m)^v(2[6-9]|[3-9]\d)\.\d+\.\d+", output); // node 26 or later: repositories that run .ts sources need its type stripping
        Assert.Contains("Python 3.", output);
        Assert.Contains("git version", output);
        Assert.Contains("2.1.283 (Claude Code)", output);         // the pinned version the adapter demands
        Assert.Contains("10001", output);                        // not root
        Assert.Contains("no-docker", output);
        Assert.Contains("pip-ok", output);
    }

    [SessionImageFact]
    public void The_image_carries_the_toolchain_manifest_the_session_note_reads_and_the_cache_mount_points_owned_by_the_session_user()
    {
        var output = Run("cat /opt/chargehand/environment.json; for e in nuget npm pip gradle maven; do stat -c '%u %n' /cache/$e; done");
        Assert.Contains("\"dotnet\": \"10.", output);
        Assert.Contains("\"docker\"", output);                  // probed, and listed as absent
        foreach (var ecosystem in new[] { "nuget", "npm", "pip", "gradle", "maven" })
            Assert.Contains($"10001 /cache/{ecosystem}", output);
    }

    [SessionImageFact]
    public void The_plugin_and_the_driver_are_in_place()
    {
        var output = Run("test -f /opt/chargehand-plugin/skills/change/SKILL.md && echo skill-ok; test -f /opt/chargehand/Chargehand.Cli.dll && echo cli-ok; dotnet /opt/chargehand/Chargehand.Cli.dll session --out /nonexistent; echo exit=$?");
        Assert.Contains("skill-ok", output);
        Assert.Contains("cli-ok", output);
        Assert.Contains("task.json", output);                    // the session verb ran and looked for its task file (an unknown verb prints the usage instead)
        Assert.DoesNotContain("usage: chargehand [--profile", output);
        Assert.Contains("exit=2", output);                       // no task file: a usage-class failure, not a crash
    }

    [SessionImageFact]
    public async Task A_workspace_is_made_on_a_real_engine_from_a_bundle_streamed_into_the_output_volume()
    {
        // The path a remote runner takes (ADR 0058): source.bundle into the run's output volume as the server's stream, then the template's helper clones from it into a new work volume.
        using var dir = new TempDir();
        var source = Path.Combine(dir.Path, "checkout");
        Directory.CreateDirectory(source);
        RepositoryBundlesTests.Git(source, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(source, "a.txt"), "one\n");
        RepositoryBundlesTests.Git(source, "add", "-A");
        RepositoryBundlesTests.Git(source, "commit", "-q", "-m", "one");
        File.WriteAllText(Path.Combine(source, "b.txt"), "two\n");
        RepositoryBundlesTests.Git(source, "add", "-A");
        RepositoryBundlesTests.Git(source, "commit", "-q", "-m", "two");
        var commit = RepositoryBundlesTests.Git(source, "rev-parse", "HEAD").Trim();
        RepositoryBundlesTests.Git(source, "checkout", "-q", "--detach", commit);
        var run = "b" + Guid.NewGuid().ToString("N")[..10];
        var engine = new Chargehand.Containers.DockerCliEngine();
        var workspace = new Chargehand.Driven.BundleWorkspaceEngine(engine, engine, new Chargehand.Driven.RepositoryBundles(Path.Combine(dir.Path, "bundles")));
        try
        {
            await engine.CreateVolumeAsync($"chargehand-out-{run}", run, default);

            await workspace.PrepareWorkspaceAsync(new Chargehand.Containers.WorkspaceSpec(run, Image!, source, $"chargehand-work-{run}", $"chargehand/{run}", commit), default);

            var psi = new ProcessStartInfo("docker", ["run", "--rm", "--user", "10001:10001", "-v", $"chargehand-work-{run}:/work:ro", "--entrypoint", "sh", Image!, "-c",
                "git config --global safe.directory '*' 2>/dev/null; cd /work && git rev-parse --abbrev-ref HEAD && git log --format=%s && cat a.txt b.txt && git status --porcelain && git config core.hooksPath"])
            { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            var error = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            Assert.True(p.ExitCode == 0, error);
            Assert.Equal(["chargehand/" + run, "two", "one", "one", "two", "/dev/null"], output.Trim().Split('\n'));
        }
        finally
        {
            await engine.RemoveVolumeAsync($"chargehand-work-{run}", default);
            await engine.RemoveVolumeAsync($"chargehand-out-{run}", default);
        }
    }
}
