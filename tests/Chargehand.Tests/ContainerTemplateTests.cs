using Chargehand.Containers;

namespace Chargehand.Tests;

/// <summary>Driven sessions (ADR 0039): the one place a <c>docker run</c> line is built, and what it can never contain.</summary>
public class ContainerTemplateTests
{
    private const string Digest = "0000000000000000000000000000000000000000000000000000000000000000";

    private static ContainerSpec Spec() =>
        new("run-1", $"registry.example/session@sha256:{Digest}", "work-run-1", "out-run-1", "net-batch-1",
            new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://egress:3128", ["CHARGEHAND_RUN_TOKEN"] = "canary-token-value" },
            MemoryMb: 4096, Cpus: 2, Pids: 512, Command: ["chargehand-session", "--run", "run-1"]);

    [Fact]
    public void Every_hardening_flag_is_present_and_the_image_is_followed_by_the_command()
    {
        var args = ContainerTemplate.RunArgs(Spec(), "/tmp/env-file");
        string After(string flag) => args[args.ToList().IndexOf(flag) + 1];
        Assert.Equal(["run", "--detach"], args.Take(2));
        Assert.Contains("--read-only", args);
        Assert.Equal("ALL", After("--cap-drop"));
        Assert.Equal("no-new-privileges", After("--security-opt"));
        Assert.Equal("10001:10001", After("--user"));
        Assert.Equal("512", After("--pids-limit"));
        Assert.Equal("4096m", After("--memory"));
        Assert.Equal("8192m", After("--memory-swap"));
        Assert.Equal("2", After("--cpus"));
        Assert.Equal("net-batch-1", After("--network"));
        Assert.Equal("chargehand.run=run-1", After("--label"));
        Assert.Contains("--init", args);
        Assert.Equal(["-v", "work-run-1:/work", "-v", "out-run-1:/out"],
            args.Where((a, i) => a == "-v" || (i > 0 && args[i - 1] == "-v")));
        var image = args.ToList().IndexOf($"registry.example/session@sha256:{Digest}");
        Assert.Equal(["chargehand-session", "--run", "run-1"], args.Skip(image + 1));
    }

    [Fact]
    public void Nothing_dangerous_can_appear()
    {
        var args = ContainerTemplate.RunArgs(Spec(), "/tmp/env-file");
        foreach (var flag in new[] { "--privileged", "--pid", "--ipc", "--device", "--cap-add", "--volumes-from", "--userns", "--uts", "--cgroupns", "--mount", "-p", "-P", "--publish", "--add-host", "--dns", "-e", "--env" })
            Assert.DoesNotContain(flag, args);
        var joined = string.Join(' ', args);
        foreach (var banned in new[] { "docker.sock", "--network host", "host", "unconfined" })
            Assert.DoesNotContain(banned, joined);
        Assert.Equal(1, args.Count(a => a == "--network"));
        Assert.Equal(2, args.Count(a => a == "-v"));
    }

    [Fact]
    public void Environment_values_go_in_a_file_never_on_the_command_line()
    {
        var args = ContainerTemplate.RunArgs(Spec(), "/tmp/env-file");
        Assert.Equal("/tmp/env-file", args[args.ToList().IndexOf("--env-file") + 1]);
        Assert.DoesNotContain(args, a => a.Contains("canary-token-value") || a.Contains("egress:3128"));
        Assert.DoesNotContain("-e", args);
    }

    [Fact]
    public void The_env_file_takes_the_names_the_runner_sets_for_a_session()
    {
        Assert.Equal(["CHARGEHAND_BASE_COMMIT=abc", "CHARGEHAND_REPOSITORY_PATH=/r"],
            ContainerTemplate.EnvFileLines(new Dictionary<string, string> { ["CHARGEHAND_REPOSITORY_PATH"] = "/r", ["CHARGEHAND_BASE_COMMIT"] = "abc" }));
    }

    [Fact]
    public void The_env_file_holds_only_names_of_the_fixed_set()
    {
        Assert.Equal(["CHARGEHAND_RUN_TOKEN=canary-token-value", "HTTPS_PROXY=http://egress:3128"], ContainerTemplate.EnvFileLines(Spec().Env));
        Assert.Throws<ArgumentException>(() => ContainerTemplate.EnvFileLines(new Dictionary<string, string> { ["LD_PRELOAD"] = "/x.so" }));
        Assert.Throws<ArgumentException>(() => ContainerTemplate.EnvFileLines(new Dictionary<string, string> { ["HTTPS_PROXY"] = "a\nCHARGEHAND_RUN_TOKEN=b" }));
        Assert.Throws<ArgumentException>(() => ContainerTemplate.EnvFileLines(new Dictionary<string, string> { ["HTTPS_PROXY"] = "a\0b" }));
    }

    [Theory]
    [InlineData("img --cap-add=ALL")]                                        // flag smuggled into the image
    [InlineData("registry.example/session:latest")]                          // a tag, not a digest
    [InlineData("registry.example/session@sha256:abc")]                      // short digest
    [InlineData("")]
    public void An_image_that_is_not_a_digest_reference_is_refused(string image) =>
        Assert.Throws<ArgumentException>(() => ContainerTemplate.RunArgs(Spec() with { ImageDigest = image }, "/tmp/e"));

    [Theory]
    [InlineData("x:/host")]
    [InlineData("--privileged")]
    [InlineData("/etc")]
    [InlineData("a b")]
    public void A_volume_or_network_name_cannot_be_a_path_or_a_flag(string name)
    {
        Assert.Throws<ArgumentException>(() => ContainerTemplate.RunArgs(Spec() with { WorkVolume = name }, "/tmp/e"));
        Assert.Throws<ArgumentException>(() => ContainerTemplate.RunArgs(Spec() with { OutVolume = name }, "/tmp/e"));
        Assert.Throws<ArgumentException>(() => ContainerTemplate.RunArgs(Spec() with { Network = name }, "/tmp/e"));
    }

    [Theory]
    [InlineData("Run 1")]
    [InlineData("--rm")]
    [InlineData("")]
    public void A_run_id_is_a_plain_token(string id) =>
        Assert.Throws<ArgumentException>(() => ContainerTemplate.RunArgs(Spec() with { RunId = id }, "/tmp/e"));

    [Fact]
    public void Limits_must_be_positive_and_bounded()
    {
        Assert.Throws<ArgumentException>(() => ContainerTemplate.RunArgs(Spec() with { MemoryMb = 0 }, "/tmp/e"));
        Assert.Throws<ArgumentException>(() => ContainerTemplate.RunArgs(Spec() with { Cpus = 0 }, "/tmp/e"));
        Assert.Throws<ArgumentException>(() => ContainerTemplate.RunArgs(Spec() with { Pids = 5 }, "/tmp/e"));
        Assert.Throws<ArgumentException>(() => ContainerTemplate.RunArgs(Spec() with { MemoryMb = 1_000_000 }, "/tmp/e"));
        Assert.Throws<ArgumentException>(() => ContainerTemplate.RunArgs(Spec() with { Command = [] }, "/tmp/e"));
        Assert.Throws<ArgumentException>(() => ContainerTemplate.RunArgs(Spec() with { Command = ["a\0b"] }, "/tmp/e"));
    }

    [Fact]
    public void A_command_word_that_looks_like_a_flag_stays_after_the_image()
    {
        var args = ContainerTemplate.RunArgs(Spec() with { Command = ["--privileged", "x"] }, "/tmp/e");
        var image = args.ToList().IndexOf($"registry.example/session@sha256:{Digest}");
        Assert.Equal(["--privileged", "x"], args.Skip(image + 1));
        Assert.DoesNotContain("--privileged", args.Take(image));
    }

    [Fact]
    public void The_workspace_helper_trusts_the_source_through_a_global_config_the_clone_s_child_process_reads()
    {
        // The source is a host directory owned by another user. `git clone` of a local path runs `git-upload-pack` as a child, which checks ownership on its
        // own and does not see `-c safe.directory` (measured on a Linux host: "detected dubious ownership in repository at '/src/.git'"). A global config in
        // the helper's writable /tmp is read by both; HOME must point there because the root filesystem is read-only.
        var lines = ContainerTemplate.WorkspaceScript.Split('\n');
        var home = Array.IndexOf(lines, "export HOME=/tmp");
        var trust = Array.IndexOf(lines, "git config --global safe.directory '*'");
        var clone = Array.FindIndex(lines, l => l.StartsWith("git clone ", StringComparison.Ordinal));
        Assert.True(home >= 0 && home < trust && trust < clone, "HOME on /tmp, then the global trust, then the clone");
    }

    [Fact]
    public void The_workspace_helper_keeps_the_images_tool_caches_out_of_the_clone_s_status()
    {
        // The session image puts the .NET CLI home and the NuGet cache in /work; a snapshot commit with `git add -A` must not take them. The script runs here with its paths pointed at temporary directories.
        using var dir = new TempDir();
        var (source, work, home) = (Path.Combine(dir.Path, "src"), Path.Combine(dir.Path, "work"), Path.Combine(dir.Path, "home"));
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(home);
        string Run(string where, params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(args[0]) { WorkingDirectory = where, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args.Skip(1))
                psi.ArgumentList.Add(a);
            psi.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(home, ".gitconfig");
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            var error = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, error);
            return output;
        }
        Run(source, "git", "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");
        Run(source, "git", "add", "-A");
        Run(source, "git", "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "-m", "base");
        var commit = Run(source, "git", "rev-parse", "HEAD").Trim();
        var script = ContainerTemplate.WorkspaceScript.Replace("/src", source).Replace("/work", work).Replace("export HOME=/tmp", $"export HOME={home}");
        Run(dir.Path, "sh", "-c", script, "prepare", "chargehand/run1", commit);

        Directory.CreateDirectory(Path.Combine(work, ".dotnet", "sdk"));
        File.WriteAllText(Path.Combine(work, ".dotnet", "sdk", "x"), "cache");
        Directory.CreateDirectory(Path.Combine(work, ".nuget", "packages", "p"));
        File.WriteAllText(Path.Combine(work, ".nuget", "packages", "p", "y"), "cache");
        File.WriteAllText(Path.Combine(work, "z.txt"), "work");

        Assert.Equal("?? z.txt", Run(work, "git", "status", "--porcelain").Trim());
    }

    private const string Image = "registry.example/session@sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private static BundleWorkspaceSpec BundleSpec(string branch = "chargehand/run1", string commit = "abc1234def5678abc1234def5678abc1234def56") =>
        new("run1", Image, "chargehand-out-run1", "chargehand-work-run1", branch, commit);

    [Fact]
    public void The_bundle_workspace_helper_has_no_network_a_read_only_root_the_bundle_volume_read_only_and_no_host_path()
    {
        var args = ContainerTemplate.BundleWorkspaceArgs(BundleSpec());
        string After(string flag) => args[args.ToList().IndexOf(flag) + 1];

        Assert.Equal("none", After("--network"));
        Assert.Contains("--read-only", args);
        Assert.Equal("ALL", After("--cap-drop"));
        Assert.Equal("no-new-privileges", After("--security-opt"));
        Assert.Equal("10001:10001", After("--user"));
        Assert.Equal(["chargehand-out-run1:/in:ro", "chargehand-work-run1:/work"], args.Select((a, i) => (a, i)).Where(x => x.a == "-v").Select(x => args[x.i + 1]));
        // No host directory is mounted: both volumes are named.
        Assert.DoesNotContain(args, a => a.Contains(":/src", StringComparison.Ordinal));
        Assert.DoesNotContain("--privileged", args);
        Assert.Equal(["prepare", "chargehand/run1", "abc1234def5678abc1234def5678abc1234def56"], args.TakeLast(3));
        // The branch and the commit reach the script as arguments, never inside its text.
        Assert.DoesNotContain("chargehand/run1", ContainerTemplate.BundleWorkspaceScript, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("branch-without-prefix", "abc1234")]
    [InlineData("chargehand/run1; rm -rf /", "abc1234")]
    [InlineData("chargehand/run1", "not-a-commit")]
    [InlineData("chargehand/run1", "$(id)")]
    public void The_bundle_workspace_helper_refuses_a_branch_or_commit_that_could_become_code(string branch, string commit) =>
        Assert.Throws<ArgumentException>(() => ContainerTemplate.BundleWorkspaceArgs(BundleSpec(branch, commit)));

    [Fact]
    public void The_governors_log_is_a_file_the_server_may_read_out_of_a_session_s_volume_and_never_write_into_one()
    {
        Assert.Contains(ContainerTemplate.GovernorFile, ContainerTemplate.OutFilesOut);
        Assert.Contains(ContainerTemplate.GovernorFile, ContainerTemplate.OptionalOutFiles);
        Assert.DoesNotContain(ContainerTemplate.GovernorFile, ContainerTemplate.OutFilesIn);
        var read = ContainerTemplate.OutFileArgs(new OutFileSpec("run1", Image, "chargehand-out-run1", ContainerTemplate.GovernorFile), write: false);
        Assert.Equal("governor.jsonl", read[^1]);
        Assert.Throws<ArgumentException>(() => ContainerTemplate.OutFileArgs(new OutFileSpec("run1", Image, "chargehand-out-run1", ContainerTemplate.GovernorFile), write: true));
        // The session's own name for it is the same constant.
        Assert.Equal(ContainerTemplate.GovernorFile, Chargehand.Driven.SessionDriver.GovernorFile);
    }

    [Fact]
    public void The_bundle_is_a_file_the_server_may_write_into_a_session_s_volume_and_nothing_may_read_back()
    {
        Assert.Contains(ContainerTemplate.SourceBundleFile, ContainerTemplate.OutFilesIn);
        Assert.DoesNotContain(ContainerTemplate.SourceBundleFile, ContainerTemplate.OutFilesOut);
        var write = ContainerTemplate.OutFileArgs(new OutFileSpec("run1", Image, "chargehand-out-run1", ContainerTemplate.SourceBundleFile), write: true);
        Assert.Equal("source.bundle", write[^1]);
        Assert.Throws<ArgumentException>(() => ContainerTemplate.OutFileArgs(new OutFileSpec("run1", Image, "chargehand-out-run1", ContainerTemplate.SourceBundleFile), write: false));
    }

    [Fact]
    public void The_bundle_workspace_script_makes_the_same_workspace_as_the_path_one_from_a_bundle_of_the_commit()
    {
        // The script runs here with its paths pointed at temporary directories, over a real bundle made as the server makes it (HEAD of a checkout detached at the commit).
        using var dir = new TempDir();
        var (source, work, home) = (Path.Combine(dir.Path, "src"), Path.Combine(dir.Path, "work"), Path.Combine(dir.Path, "home"));
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(home);
        string Run(string where, params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(args[0]) { WorkingDirectory = where, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args.Skip(1))
                psi.ArgumentList.Add(a);
            psi.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(home, ".gitconfig");
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            var error = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, error);
            return output;
        }
        Run(source, "git", "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");
        Run(source, "git", "add", "-A");
        Run(source, "git", "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "-m", "one");
        File.WriteAllText(Path.Combine(source, "b.txt"), "b");
        Run(source, "git", "add", "-A");
        Run(source, "git", "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "-m", "two");
        var commit = Run(source, "git", "rev-parse", "HEAD").Trim();
        Run(source, "git", "checkout", "-q", "--detach", commit);
        var bundle = Path.Combine(dir.Path, "source.bundle");
        Run(source, "git", "bundle", "create", bundle, "HEAD");

        var script = ContainerTemplate.BundleWorkspaceScript.Replace("/in/source.bundle", bundle).Replace("/work", work).Replace("export HOME=/tmp", $"export HOME={home}");
        Run(dir.Path, "sh", "-c", script, "prepare", "chargehand/run1", commit);

        Assert.Equal("chargehand/run1", Run(work, "git", "rev-parse", "--abbrev-ref", "HEAD").Trim());
        Assert.Equal(commit, Run(work, "git", "rev-parse", "HEAD").Trim());
        Assert.Equal(["two", "one"], Run(work, "git", "log", "--format=%s").Trim().Split('\n'));
        Assert.Equal("ab", File.ReadAllText(Path.Combine(work, "a.txt")) + File.ReadAllText(Path.Combine(work, "b.txt")));
        Assert.Equal("", Run(work, "git", "status", "--porcelain").Trim());
        Assert.Equal("/dev/null", Run(work, "git", "config", "core.hooksPath").Trim());
        // The toolchains' caches stay out of the snapshot commit, as for the path workspace.
        Directory.CreateDirectory(Path.Combine(work, ".nuget", "packages"));
        File.WriteAllText(Path.Combine(work, ".nuget", "packages", "y"), "cache");
        Assert.Equal("", Run(work, "git", "status", "--porcelain").Trim());
    }
}
