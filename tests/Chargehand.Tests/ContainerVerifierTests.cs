using System.Diagnostics;
using System.Text.Json;
using Chargehand.Containers;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>ADR 0039: the tests of a session's branch run in a fresh container that holds no model credential, on a clean workspace, with only the bundle carried over.</summary>
public class ContainerVerifierTests
{
    private const string Image = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private sealed class FakeEngine : IContainerEngine, IWorkspaceEngine
    {
        public List<string> Calls { get; } = [];
        public ContainerSpec? Started { get; private set; }
        public WorkspaceSpec? Prepared { get; private set; }
        public Queue<ContainerState> States { get; } = new([new ContainerState(ContainerStatus.Running, null, false), new ContainerState(ContainerStatus.Exited, 0, false)]);
        public string Logs { get; set; } = "";

        public Task<string> StartAsync(ContainerSpec spec, CancellationToken ct) { Started = spec; Calls.Add("start"); return Task.FromResult("cid1"); }
        public Task SignalAsync(string id, string signal, CancellationToken ct) { Calls.Add($"signal {id} {signal}"); return Task.CompletedTask; }
        public Task<ContainerState> InspectAsync(string id, CancellationToken ct) { Calls.Add("inspect"); return Task.FromResult(States.Count > 1 ? States.Dequeue() : States.Peek()); }
        public Task RemoveAsync(string id, CancellationToken ct) { Calls.Add($"rm {id}"); return Task.CompletedTask; }
        public Task KillAllAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string> LogsTailAsync(string id, int bytes, CancellationToken ct) { Calls.Add("logs"); return Task.FromResult(Logs); }
        public Task CreateVolumeAsync(string name, string runId, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveVolumeAsync(string name, CancellationToken ct) { Calls.Add($"volume-rm {name}"); return Task.CompletedTask; }
        public Task CreateNetworkAsync(string name, string batchId, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveNetworkAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> StartEgressAsync(EgressSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public Task ConnectNetworkAsync(string container, string network, CancellationToken ct) => throw new NotSupportedException();
        public Task PrepareWorkspaceAsync(WorkspaceSpec spec, CancellationToken ct) { Prepared = spec; Calls.Add("prepare"); return Task.CompletedTask; }
    }

    private static ContainerVerifier Verifier(FakeEngine engine, TimeSpan? poll = null) =>
        new(engine, engine, new ContainerVerifierSettings(Image, "/srv/checkouts/repo-abc1234", "abc1234def", "chargehand-net-b1", "http://run1:x@chargehand-egress-b1:3128", 4096, 2, 512),
            poll ?? TimeSpan.FromMilliseconds(5));

    private static readonly string[] NpmTest = ["npm", "test"];

    private static string Line(int exit, string tail = "ok", bool timedOut = false) =>
        VerifyBranchCli.Marker + JsonSerializer.Serialize(new { argv = NpmTest, exit_code = exit, output_tail = tail, timed_out = timedOut, duration_ms = 4200, commit = "c0ffee" });

    private static BranchVerification Request(int seconds = 600) => new("run1", "/out/chargehand.bundle", "chargehand/run1", ["npm", "test"], TimeSpan.FromSeconds(seconds));

    [Fact]
    public async Task It_replaces_the_session_workspace_with_a_fresh_one_and_starts_the_verification_with_no_model_credential()
    {
        var engine = new FakeEngine { Logs = "noise\n" + Line(0) + "\n" };
        var record = await Verifier(engine).VerifyAsync(Request(), default);
        Assert.Equal(["rm chargehand-run1", "volume-rm chargehand-work-run1", "prepare", "start"], engine.Calls.Take(4));
        Assert.Equal(new WorkspaceSpec("run1", Image, "/srv/checkouts/repo-abc1234", "chargehand-work-run1", "chargehand/run1", "abc1234def"), engine.Prepared);
        var spec = engine.Started!;
        Assert.Equal(("run1", "chargehand-work-run1", "chargehand-out-run1", "chargehand-net-b1"), (spec.RunId, spec.WorkVolume, spec.OutVolume, spec.Network));
        Assert.Equal(["verify-branch", "--bundle", "/out/chargehand.bundle", "--branch", "chargehand/run1", "--timeout-seconds", "600", "--", "npm", "test"], spec.Command);
        Assert.Equal(["HTTP_PROXY", "HTTPS_PROXY"], spec.Env.Keys.Order());
        Assert.DoesNotContain(spec.Env.Keys, k => k.Contains("ANTHROPIC", StringComparison.Ordinal) || k.Contains("OAUTH", StringComparison.Ordinal) || k.Contains("TOKEN", StringComparison.Ordinal));
        Assert.Equal(0, record.ExitCode);
        Assert.True(record.Network);
        Assert.Equal(TimeSpan.FromMilliseconds(4200), record.Duration);
        Assert.Equal(["npm", "test"], record.Argv);
        Assert.Contains("rm cid1", engine.Calls);                             // the verification container is removed when it is read
    }

    [Fact]
    public async Task A_failing_test_is_its_exit_code_and_output_tail()
    {
        var engine = new FakeEngine { Logs = Line(1, "2 failed") };
        var record = await Verifier(engine).VerifyAsync(Request(), default);
        Assert.Equal(1, record.ExitCode);
        Assert.Equal("2 failed", record.OutputTail);
        Assert.False(record.TimedOut);
    }

    [Fact]
    public async Task A_timeout_reported_by_the_command_is_kept()
    {
        var record = await Verifier(new FakeEngine { Logs = Line(-1, "", timedOut: true) }).VerifyAsync(Request(), default);
        Assert.True(record.TimedOut);
    }

    [Fact]
    public async Task A_container_that_never_answers_is_killed_at_the_deadline_and_reported_timed_out()
    {
        var engine = new FakeEngine();
        engine.States.Clear();
        engine.States.Enqueue(new ContainerState(ContainerStatus.Running, null, false));
        var sw = Stopwatch.StartNew();
        var record = await new ContainerVerifier(engine, engine, new ContainerVerifierSettings(Image, "/s/r", "abc1234", "n", "http://p", 4096, 2, 512), TimeSpan.FromMilliseconds(5), slack: TimeSpan.Zero)
            .VerifyAsync(Request(seconds: 1), default);
        Assert.True(record.TimedOut);
        Assert.Contains("signal cid1 SIGKILL", engine.Calls);
        Assert.Contains("rm cid1", engine.Calls);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_container_that_dies_without_a_record_is_a_failure_with_its_logs_and_an_oom_note()
    {
        var engine = new FakeEngine { Logs = "Killed" };
        engine.States.Clear();
        engine.States.Enqueue(new ContainerState(ContainerStatus.Exited, 137, OomKilled: true));
        var record = await Verifier(engine).VerifyAsync(Request(), default);
        Assert.NotEqual(0, record.ExitCode);
        Assert.Contains("memory", record.OutputTail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Killed", record.OutputTail);
    }

    [Fact]
    public async Task Logs_with_a_forged_record_earlier_are_read_from_the_last_line_only()
    {
        var engine = new FakeEngine { Logs = Line(0, "forged by the tests") + "\n" + Line(1, "the real one") + "\n" };
        Assert.Equal(1, (await Verifier(engine).VerifyAsync(Request(), default)).ExitCode);
    }

    [Fact]
    public async Task Nothing_is_left_behind_when_the_run_is_cancelled()
    {
        var engine = new FakeEngine();
        engine.States.Clear();
        engine.States.Enqueue(new ContainerState(ContainerStatus.Running, null, false));
        using var cts = new CancellationTokenSource();
        var task = Verifier(engine).VerifyAsync(Request(), cts.Token);
        await Task.Delay(50);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Contains("rm cid1", engine.Calls);
    }

    // ---- a real engine ----

    private static string? SessionImage => Environment.GetEnvironmentVariable("CHARGEHAND_TEST_SESSION_IMAGE");

    private sealed class SessionImageFactAttribute : FactAttribute
    {
        public SessionImageFactAttribute() => Skip = string.IsNullOrEmpty(SessionImage)
            ? "set CHARGEHAND_TEST_SESSION_IMAGE to a locally built session image"
            : DockerFactAttribute.Reason(egress: false) is { } r && r.StartsWith("no Docker", StringComparison.Ordinal) ? r : null;
    }

    private static string Sh(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{file} {string.Join(' ', args)}: {text}");
        return text;
    }

    [SessionImageFact]
    public async Task On_a_real_engine_the_tests_run_on_the_bundles_branch_in_a_fresh_container_and_a_failure_is_reported()
    {
        using var dir = new TempDir();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(dir.Path, "source")).FullName;
        Sh("git", "-C", source, "init", "-q", "-b", "main");
        File.WriteAllText(System.IO.Path.Combine(source, "a.txt"), "a");
        Sh("git", "-C", source, "add", "-A");
        Sh("git", "-C", source, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "-m", "base");
        var baseCommit = Sh("git", "-C", source, "rev-parse", "HEAD").Trim();
        var run = "v" + Guid.NewGuid().ToString("N")[..8];
        var session = System.IO.Path.Combine(dir.Path, "session");
        Sh("git", "clone", "-q", "--template=", source, session);
        Sh("git", "-C", session, "checkout", "-q", "-b", $"chargehand/{run}");
        File.WriteAllText(System.IO.Path.Combine(session, "retry.txt"), "retry");
        Sh("git", "-C", session, "add", "-A");
        Sh("git", "-C", session, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "-m", "feat");
        var bundleDir = Directory.CreateDirectory(System.IO.Path.Combine(dir.Path, "bundle")).FullName;
        Sh("git", "-C", session, "bundle", "create", System.IO.Path.Combine(bundleDir, "chargehand.bundle"), $"chargehand/{run}", "--not", "--remotes");

        var engine = new DockerCliEngine();
        var outVolume = RunnerNames.Out(run);
        try
        {
            await engine.CreateVolumeAsync(outVolume, run, default);
            // The session's output volume, as it would be after the session: the bundle is in it.
            Sh("docker", "run", "--rm", "-v", $"{outVolume}:/out", "-v", $"{bundleDir}:/in:ro", "--entrypoint", "cp", SessionImage!, "/in/chargehand.bundle", "/out/chargehand.bundle");
            var settings = new ContainerVerifierSettings(SessionImage!, source, baseCommit, "none", "http://unused:3128", 1024, 1, 256);
            var verifier = new ContainerVerifier(engine, engine, settings);

            var pass = await verifier.VerifyAsync(new BranchVerification(run, "/out/chargehand.bundle", $"chargehand/{run}", ["sh", "-c", "test -f retry.txt && echo saw-the-change"], TimeSpan.FromSeconds(60)), default);
            Assert.Equal(0, pass.ExitCode);
            Assert.Contains("saw-the-change", pass.OutputTail);

            var fail = await verifier.VerifyAsync(new BranchVerification(run, "/out/chargehand.bundle", $"chargehand/{run}", ["sh", "-c", "test -f not-there.txt"], TimeSpan.FromSeconds(60)), default);
            Assert.NotEqual(0, fail.ExitCode);

            var slow = await verifier.VerifyAsync(new BranchVerification(run, "/out/chargehand.bundle", $"chargehand/{run}", ["sh", "-c", "sleep 60"], TimeSpan.FromSeconds(2)), default);
            Assert.True(slow.TimedOut);
        }
        finally
        {
            await engine.RemoveAsync($"chargehand-{run}", default);
            await engine.RemoveVolumeAsync(outVolume, default);
            await engine.RemoveVolumeAsync(RunnerNames.Work(run), default);
        }
    }
}
