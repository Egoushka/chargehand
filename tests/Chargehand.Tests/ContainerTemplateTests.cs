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
}
