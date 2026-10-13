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
    public async Task The_end_of_a_transcript_is_read_with_a_tail_helper_and_a_missing_one_reads_as_false()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeDocker(dir, "printf 'the end'");
        var engine = new DockerCliEngine("/bin/sh", [docker]);
        using var read = new MemoryStream();

        Assert.True(await engine.ReadOutTailAsync(Spec("stream.jsonl"), 4096, read, default));

        Assert.Equal("the end", System.Text.Encoding.UTF8.GetString(read.ToArray()));
        var calls = File.ReadAllText(log);
        Assert.Single(File.ReadAllLines(log), l => l.StartsWith("run --rm --init -i --name chargehand-xfer-run1", StringComparison.Ordinal));
        Assert.EndsWith("transfer stream.jsonl 4096\n", calls, StringComparison.Ordinal);
        Assert.Contains("chargehand-out-run1:/out:ro", calls, StringComparison.Ordinal);
        using var dir2 = new TempDir();
        Assert.False(await new DockerCliEngine("/bin/sh", [FakeDocker(dir2, "exit 3").Script]).ReadOutTailAsync(Spec("stream.jsonl"), 4096, new MemoryStream(), default));
        using var dir3 = new TempDir();
        await Assert.ThrowsAsync<ChargehandException>(() => Quick(FakeDocker(dir3, "exit 1").Script).ReadOutTailAsync(Spec("stream.jsonl"), 4096, new MemoryStream(), default));
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
        var failing = Quick(FakeDocker(dir2, "echo 'no such volume' >&2; exit 125").Script);
        var e = await Assert.ThrowsAsync<ChargehandException>(() => failing.ReadOutFileAsync(Spec("chargehand.bundle"), read, default));
        Assert.Equal(ErrorCode.ContainerUnavailable, e.Code);
        Assert.Contains("no such volume", e.Message);
        await Assert.ThrowsAsync<ChargehandException>(() => failing.WriteOutFileAsync(Spec("task.json"), new byte[] { 1 }, default));
    }

    /// <summary>Like <see cref="FakeDocker"/>, but a <c>run</c> is logged as the one word: its helper script spans lines, which would blur the count of calls.</summary>
    private static (string Script, string Log) FakeEngine(TempDir dir, string body)
    {
        var log = System.IO.Path.Combine(dir.Path, "calls.log");
        return (dir.Write("docker", $"#!/bin/sh\nif [ \"$1\" = run ]; then echo run >> '{log}'; else printf '%s\\n' \"$*\" >> '{log}'; fi\n{body}\n"), log);
    }

    [DockerFact]
    public async Task On_a_real_engine_a_helper_left_created_under_the_run_name_no_longer_blocks_the_transfer()
    {
        var run = "o" + Guid.NewGuid().ToString("N")[..8];
        var volume = "chargehand-out-" + run;
        var engine = new DockerCliEngine();
        var spec = new OutFileSpec(run, DockerFactAttribute.Image!, volume, "chargehand.bundle");
        try
        {
            await engine.CreateVolumeAsync(volume, run, default);
            // What a call killed between create and start leaves: the container exists, labelled, and never ran.
            void Leave() => Docker("create", "--name", ContainerTemplate.TransferPrefix + run, "--label", $"{ContainerTemplate.RunLabel}={run}", DockerFactAttribute.Image!, "true");
            Leave();
            using var read = new MemoryStream();
            Assert.False(await engine.ReadOutFileAsync(spec, read, default));

            Leave();
            Assert.Contains(ContainerTemplate.TransferPrefix + run, await engine.RemoveStaleHelpersAsync(default));
            Assert.DoesNotContain(ContainerTemplate.TransferPrefix + run, (await engine.ListAsync(default)).Containers);
        }
        finally
        {
            await engine.RemoveAsync(ContainerTemplate.TransferPrefix + run, default);
            await engine.RemoveVolumeAsync(volume, default);
        }
    }

    private static void Docker(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
    }

    private static DockerCliEngine Quick(string script) => new("/bin/sh", [script], retryEvery: TimeSpan.Zero);

    private const string Conflict = "docker: Error response from daemon: Conflict. The container name \"/chargehand-xfer-run1\" is already in use by container \"124189f9\". You have to remove (or rename) that container to be able to reuse that name.";

    private static string[] Verbs(string log) => [.. File.ReadAllLines(log).Select(l => l.Split(' ')[0])];

    [Fact]
    public async Task A_stopped_helper_left_under_the_same_name_is_removed_and_the_transfer_runs_again()
    {
        using var dir = new TempDir();
        var stale = System.IO.Path.Combine(dir.Path, "stale");
        File.WriteAllText(stale, "");
        var (docker, log) = FakeEngine(dir, $$"""
            case "$1" in
              run) if [ -e '{{stale}}' ]; then echo '{{Conflict}}' >&2; exit 125; fi; printf 'bundle-bytes';;
              inspect) echo run1;;
              rm) rm -f '{{stale}}';;
            esac
            """);
        using var read = new MemoryStream();
        Assert.True(await Quick(docker).ReadOutFileAsync(Spec("chargehand.bundle"), read, default));
        Assert.Equal("bundle-bytes", System.Text.Encoding.UTF8.GetString(read.ToArray()));
        Assert.Equal(["run", "inspect", "rm", "run"], Verbs(log));
        // No -f: a helper that is running is somebody's transfer in progress, and docker refuses to remove it.
        Assert.Contains("rm chargehand-xfer-run1", File.ReadAllLines(log));
    }

    [Fact]
    public async Task A_helper_that_is_still_running_is_waited_for_not_killed_and_the_conflict_is_reported_in_the_end()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeEngine(dir, $$"""
            case "$1" in
              run) echo '{{Conflict}}' >&2; exit 125;;
              inspect) echo run1;;
              rm) echo 'Error response from daemon: cannot remove a running container' >&2; exit 1;;
            esac
            """);
        var e = await Assert.ThrowsAsync<ChargehandException>(() => Quick(docker).ReadOutFileAsync(Spec("chargehand.bundle"), new MemoryStream(), default));
        Assert.Equal(ErrorCode.ContainerUnavailable, e.Code);
        Assert.Contains("is already in use", e.Message);
        Assert.Equal(4, Verbs(log).Count(v => v == "run"));
        Assert.DoesNotContain(File.ReadAllLines(log), l => l.StartsWith("rm -f", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_conflict_with_a_container_that_is_not_chargehands_is_left_alone()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeEngine(dir, $$"""
            case "$1" in
              run) echo '{{Conflict}}' >&2; exit 125;;
              inspect) echo '<no value>';;
            esac
            """);
        await Assert.ThrowsAsync<ChargehandException>(() => Quick(docker).ReadOutFileAsync(Spec("chargehand.bundle"), new MemoryStream(), default));
        Assert.DoesNotContain("rm", Verbs(log));
    }

    [Fact]
    public async Task An_engine_that_blinks_is_asked_again_for_a_read_and_for_a_write()
    {
        using var dir = new TempDir();
        var count = System.IO.Path.Combine(dir.Path, "count");
        var stdin = System.IO.Path.Combine(dir.Path, "stdin");
        var (docker, log) = FakeEngine(dir, $$"""
            case "$1" in
              run)
                n=$(cat '{{count}}' 2>/dev/null || echo 0); n=$((n+1)); echo $n > '{{count}}'
                if [ $n -lt 3 ]; then echo 'error during connect: connection refused' >&2; exit 125; fi
                case "$*" in *'/out:ro'*) printf 'bundle-bytes';; *) cat > '{{stdin}}';; esac;;
            esac
            """);
        var engine = Quick(docker);
        using var read = new MemoryStream();
        Assert.True(await engine.ReadOutFileAsync(Spec("chargehand.bundle"), read, default));
        Assert.Equal("bundle-bytes", System.Text.Encoding.UTF8.GetString(read.ToArray()));
        Assert.Equal(["run", "run", "run"], Verbs(log));

        File.Delete(count);
        await engine.WriteOutFileAsync(Spec("task.json"), "{\"goal\":\"x\"}"u8.ToArray(), default);
        Assert.Equal("{\"goal\":\"x\"}", File.ReadAllText(stdin));
    }

    [Fact]
    public async Task A_failure_that_does_not_go_away_ends_after_a_few_tries_with_docker_s_own_words()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeEngine(dir, "echo 'error during connect: connection refused' >&2; exit 125");
        var e = await Assert.ThrowsAsync<ChargehandException>(() => Quick(docker).ReadOutFileAsync(Spec("chargehand.bundle"), new MemoryStream(), default));
        Assert.Contains("connection refused", e.Message);
        Assert.Equal(4, File.ReadAllLines(log).Length);
    }

    [Fact]
    public async Task A_missing_file_and_a_failure_after_bytes_were_copied_are_not_asked_again()
    {
        using var dir = new TempDir();
        var (missing, missingLog) = FakeEngine(dir, "exit 3");
        Assert.False(await Quick(missing).ReadOutFileAsync(Spec("chargehand.bundle"), new MemoryStream(), default));
        Assert.Single(File.ReadAllLines(missingLog));

        using var dir2 = new TempDir();
        var (half, halfLog) = FakeEngine(dir2, "printf half; echo 'the daemon went away' >&2; exit 125");
        using var read = new MemoryStream();
        await Assert.ThrowsAsync<ChargehandException>(() => Quick(half).ReadOutFileAsync(Spec("chargehand.bundle"), read, default));
        Assert.Single(File.ReadAllLines(halfLog));
        Assert.Equal("half", System.Text.Encoding.UTF8.GetString(read.ToArray()));
    }

    [Fact]
    public async Task A_transfer_cancelled_in_flight_removes_the_helper_docker_may_have_created_and_never_started()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeEngine(dir, "case \"$1\" in run) sleep 30;; esac");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Quick(docker).ReadOutFileAsync(Spec("chargehand.bundle"), new MemoryStream(), cts.Token));
        Assert.Contains("rm -f chargehand-xfer-run1", File.ReadAllLines(log));
    }

    [Fact]
    public async Task Leftover_helpers_that_are_not_running_are_removed_and_nothing_else()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeEngine(dir, """
            case "$1" in
              ps) printf 'chargehand-xfer-run1\nchargehand-run2\nchargehand-xfer-run3\nchargehand-prep-run4\nchargehand-egress-b1\nchargehand-svc-pg-b1\n';;
              rm) if [ "$2" = chargehand-xfer-run3 ]; then exit 1; fi;;
            esac
            """);
        var removed = await Quick(docker).RemoveStaleHelpersAsync(default);
        // run3 could not be removed (it ended meanwhile, or the engine refused): it is not counted, and the rest still go.
        Assert.Equal(["chargehand-xfer-run1", "chargehand-prep-run4"], removed);
        var calls = File.ReadAllLines(log);
        var listed = calls[0];
        foreach (var part in new[] { "ps -a", "label=chargehand.run", "status=created", "status=exited", "status=dead" })
            Assert.Contains(part, listed);
        Assert.DoesNotContain("status=running", listed);
        Assert.Equal(["rm chargehand-xfer-run1", "rm chargehand-xfer-run3", "rm chargehand-prep-run4"], calls[1..]);
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

    [Fact]
    public async Task A_stream_is_piped_to_the_helper_as_it_is_read_and_the_helper_gets_the_bundle_volume_to_write()
    {
        using var dir = new TempDir();
        var stdin = System.IO.Path.Combine(dir.Path, "stdin");
        var (docker, log) = FakeDocker(dir, $"cat > '{stdin}'");
        var engine = new DockerCliEngine("/bin/sh", [docker]);
        var bytes = new byte[300_000];
        new Random(7).NextBytes(bytes);

        await engine.WriteOutStreamAsync(Spec("source.bundle"), new MemoryStream(bytes), bytes.Length, default);

        Assert.Equal(bytes, File.ReadAllBytes(stdin));
        var call = File.ReadAllText(log);
        Assert.Contains("run --rm --init -i --name chargehand-xfer-run1", call);
        Assert.Contains("-v chargehand-out-run1:/out ", call);
        Assert.EndsWith("transfer source.bundle", call.Trim());
    }

    [Fact]
    public async Task A_stream_the_helper_does_not_take_is_container_unavailable()
    {
        using var dir = new TempDir();
        var engine = new DockerCliEngine("/bin/sh", [FakeDocker(dir, "echo 'no space left on device' >&2; exit 1").Script]);

        var e = await Assert.ThrowsAsync<ChargehandException>(() => engine.WriteOutStreamAsync(Spec("source.bundle"), new MemoryStream(new byte[1000]), 1000, default));

        Assert.Equal(ErrorCode.ContainerUnavailable, e.Code);
        Assert.Contains("no space left", e.Message);
    }

    [Fact]
    public async Task The_bundle_workspace_makes_the_work_volume_then_runs_the_helper_and_a_failure_removes_the_volume()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeDocker(dir, "case \"$*\" in *'/in:ro'*) echo 'fatal: bad bundle' >&2; exit 128;; esac");
        var engine = new DockerCliEngine("/bin/sh", [docker]);
        var spec = new BundleWorkspaceSpec("run1", Image, "chargehand-out-run1", "chargehand-work-run1", "chargehand/run1", "abc1234def5678abc1234def5678abc1234def56");

        var e = await Assert.ThrowsAsync<ChargehandException>(() => engine.PrepareBundleWorkspaceAsync(spec, default));

        Assert.Contains("fatal: bad bundle", e.Message);
        var calls = File.ReadAllText(log);
        var (create, run, remove) = (calls.IndexOf("volume create --label chargehand.run=run1 chargehand-work-run1", StringComparison.Ordinal), calls.IndexOf("run --rm --init --name chargehand-prep-run1", StringComparison.Ordinal),
            calls.IndexOf("volume rm -f chargehand-work-run1", StringComparison.Ordinal));
        Assert.True(create >= 0 && create < run && run < remove, calls);
        Assert.Contains("-v chargehand-out-run1:/in:ro -v chargehand-work-run1:/work", calls);
    }

    /// <summary>A <c>docker</c> that answers a second <c>run --name X</c> with the daemon's conflict until <c>rm X</c> (chargehand's own container, not running): what a helper left Created by an interrupted run does.</summary>
    private static (string Script, string Log) ConflictingDocker(TempDir dir, params string[] leftover)
    {
        var state = System.IO.Path.Combine(dir.Path, "exists");
        Directory.CreateDirectory(state);
        foreach (var name in leftover)
            File.WriteAllText(System.IO.Path.Combine(state, name), "");
        return FakeDocker(dir, $"""
            case "$1" in
              inspect) for a in "$@"; do last="$a"; done
                       if [ -e '{state}'/"$last" ]; then echo run1; else echo "Error: No such object: $last" >&2; exit 1; fi;;
              rm) for a in "$@"; do rm -f '{state}'/"$a"; done;;
              run) name=""; prev=""
                   for a in "$@"; do if [ "$prev" = "--name" ]; then name="$a"; fi; prev="$a"; done
                   if [ -e '{state}'/"$name" ]; then echo "docker: Error response from daemon: Conflict. The container name \"/$name\" is already in use." >&2; exit 125; fi
                   cat > /dev/null;;
            esac
            """);
    }

    [Fact]
    public async Task A_helper_left_Created_by_an_interrupted_run_does_not_block_the_bundle_transfer_or_its_workspace()
    {
        using var dir = new TempDir();
        var (docker, log) = ConflictingDocker(dir, "chargehand-xfer-run1", "chargehand-prep-run1");
        var engine = new DockerCliEngine("/bin/sh", [docker]);

        await engine.WriteOutStreamAsync(Spec("source.bundle"), new MemoryStream(new byte[100]), 100, default);
        await engine.PrepareBundleWorkspaceAsync(new BundleWorkspaceSpec("run1", Image, "chargehand-out-run1", "chargehand-work-run1", "chargehand/run1", "abc1234def5678abc1234def5678abc1234def56"), default);

        var calls = File.ReadAllText(log);
        // Removed without -f, as the transfers' own retry does: a helper that is running is another transfer of the run and is not killed.
        Assert.True(calls.IndexOf("\nrm chargehand-xfer-run1", StringComparison.Ordinal) < calls.IndexOf("run --rm --init -i --name chargehand-xfer-run1", StringComparison.Ordinal));
        Assert.True(calls.IndexOf("\nrm chargehand-prep-run1", StringComparison.Ordinal) < calls.LastIndexOf("run --rm --init --name chargehand-prep-run1", StringComparison.Ordinal));
        Assert.DoesNotContain("rm -f chargehand-", calls);
    }

    [Fact]
    public async Task A_bundle_transfer_and_a_bundle_workspace_cancelled_in_flight_remove_the_helper_docker_may_have_created()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeDocker(dir, "case \"$1\" in run) sleep 30;; esac");
        var engine = new DockerCliEngine("/bin/sh", [docker]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.WriteOutStreamAsync(Spec("source.bundle"), new MemoryStream(new byte[10]), 10, cts.Token));
        using var cts2 = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.PrepareBundleWorkspaceAsync(
            new BundleWorkspaceSpec("run1", Image, "chargehand-out-run1", "chargehand-work-run1", "chargehand/run1", "abc1234def5678abc1234def5678abc1234def56"), cts2.Token));

        var calls = File.ReadAllLines(log);
        Assert.Contains("rm -f chargehand-xfer-run1", calls);
        Assert.Contains("rm -f chargehand-prep-run1", calls);
    }

    private static readonly string PrepConflict = Conflict.Replace("xfer", "prep", StringComparison.Ordinal);

    private static readonly WorkspaceSpec CloneSpec = new("run1", Image, "/srv/checkouts/r", "chargehand-work-run1", "chargehand/run1", "abc1234");

    private static readonly BundleWorkspaceSpec BundleSpec = new("run1", Image, "chargehand-out-run1", "chargehand-work-run1", "chargehand/run1", "abc1234def5678abc1234def5678abc1234def56");

    private static Task Prepare(DockerCliEngine engine, bool bundle, CancellationToken ct = default) =>
        bundle ? engine.PrepareBundleWorkspaceAsync(BundleSpec, ct) : engine.PrepareWorkspaceAsync(CloneSpec, ct);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_workspace_helper_left_Created_under_the_run_name_is_removed_and_the_workspace_is_prepared_again(bool bundle)
    {
        using var dir = new TempDir();
        var stale = System.IO.Path.Combine(dir.Path, "stale");
        File.WriteAllText(stale, "");
        var (docker, log) = FakeEngine(dir, $$"""
            case "$1" in
              run) if [ -e '{{stale}}' ]; then echo '{{PrepConflict}}' >&2; exit 125; fi;;
              inspect) echo run1;;
              rm) rm -f '{{stale}}';;
            esac
            """);

        await Prepare(Quick(docker), bundle);

        Assert.Equal(["volume", "run", "inspect", "rm", "run"], Verbs(log));
        var calls = File.ReadAllLines(log);
        Assert.Contains("rm chargehand-prep-run1", calls);
        Assert.DoesNotContain(calls, c => c.StartsWith("volume rm", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_engine_that_blinks_is_asked_again_for_a_workspace_helper_and_the_work_volume_stays(bool bundle)
    {
        using var dir = new TempDir();
        var count = System.IO.Path.Combine(dir.Path, "count");
        var (docker, log) = FakeEngine(dir, $$"""
            case "$1" in
              run)
                n=$(cat '{{count}}' 2>/dev/null || echo 0); n=$((n+1)); echo $n > '{{count}}'
                if [ $n -lt 3 ]; then echo 'error during connect: connection refused' >&2; exit 125; fi;;
            esac
            """);

        await Prepare(Quick(docker), bundle);

        Assert.Equal(["volume", "run", "run", "run"], Verbs(log));
        Assert.DoesNotContain(File.ReadAllLines(log), c => c.StartsWith("volume rm", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_workspace_helper_name_held_by_a_container_that_is_not_chargehands_is_left_alone_and_the_volume_is_removed(bool bundle)
    {
        using var dir = new TempDir();
        var (docker, log) = FakeEngine(dir, $$"""
            case "$1" in
              run) echo '{{PrepConflict}}' >&2; exit 125;;
              inspect) echo '<no value>';;
            esac
            """);

        var e = await Assert.ThrowsAsync<ChargehandException>(() => Prepare(Quick(docker), bundle));

        Assert.Equal(ErrorCode.ContainerUnavailable, e.Code);
        Assert.Contains("is already in use", e.Message);
        Assert.Equal(4, Verbs(log).Count(v => v == "run"));
        Assert.DoesNotContain("rm", Verbs(log));
        Assert.Contains("volume rm -f chargehand-work-run1", File.ReadAllLines(log));
    }

    [Fact]
    public async Task A_workspace_cancelled_in_flight_removes_the_helper_docker_may_have_created_and_never_started()
    {
        using var dir = new TempDir();
        var (docker, log) = FakeEngine(dir, "case \"$1\" in run) sleep 30;; esac");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Quick(docker).PrepareWorkspaceAsync(CloneSpec, cts.Token));

        Assert.Contains("rm -f chargehand-prep-run1", File.ReadAllLines(log));
    }
}
