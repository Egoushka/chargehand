using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Chargehand.Bench;
using Chargehand.Driven;
using Chargehand.Kernel;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Tests;

/// <summary>An acceptance check is made like a task's own verification: new volumes, a network whose only way out is the allowlist proxy, a bundle of the branch, and one record as the verdict.
/// A fake engine records what is asked of it; the bundle is made with real git.</summary>
public sealed class BenchAcceptanceTests : IDisposable
{
    private const string Image = "registry.example/session@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string[] Identity = ["-c", "user.name=t", "-c", "user.email=t@example.test", "-c", "commit.gpgsign=false"];

    private readonly TempDir _dir = new();
    private readonly string _repo;
    private readonly string _base;

    public BenchAcceptanceTests()
    {
        _repo = _dir.Path;
        Git("init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "one\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "base");
        _base = Git("rev-parse", "HEAD").Trim();
        Git("checkout", "-q", "-b", "chargehand/run-1");
        File.WriteAllText(Path.Combine(_repo, "b.txt"), "two\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "work");
    }

    public void Dispose() => _dir.Dispose();

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in Identity.Concat(args))
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        psi.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
        return output;
    }

    private sealed class FakeEngine(Func<string> logs, Func<ContainerState>? state = null) : IContainerEngine, IWorkspaceEngine, IOutVolumeEngine
    {
        public List<string> Calls { get; } = [];
        public ContainerSpec? Started { get; private set; }
        public EgressSpec? Egress { get; private set; }
        public WorkspaceSpec? Workspace { get; private set; }
        public byte[]? Bundle { get; private set; }
        public bool FailWorkspace { get; set; }

        public Task<string> StartAsync(ContainerSpec spec, CancellationToken ct)
        {
            Calls.Add("start");
            Started = spec;
            return Task.FromResult("container-1");
        }

        public Task SignalAsync(string id, string signal, CancellationToken ct) => Record($"signal {signal}");

        public Task<ContainerState> InspectAsync(string id, CancellationToken ct) => Task.FromResult(state?.Invoke() ?? new ContainerState(ContainerStatus.Exited, 0, false));

        public Task RemoveAsync(string id, CancellationToken ct) => Record($"remove {id}");

        public Task KillAllAsync(CancellationToken ct) => Record("kill-all");

        public Task<string> LogsTailAsync(string id, int bytes, CancellationToken ct) => Task.FromResult(logs());

        public Task CreateVolumeAsync(string name, string runId, CancellationToken ct) => Record($"create-volume {name}");

        public Task RemoveVolumeAsync(string name, CancellationToken ct) => Record($"remove-volume {name}");

        public Task CreateNetworkAsync(string name, string batchId, CancellationToken ct) => Record($"create-network {name}");

        public Task RemoveNetworkAsync(string name, CancellationToken ct) => Record($"remove-network {name}");

        public Task<string> StartEgressAsync(EgressSpec spec, CancellationToken ct)
        {
            Calls.Add("egress");
            Egress = spec;
            return Task.FromResult("egress-1");
        }

        public Task ConnectNetworkAsync(string container, string network, CancellationToken ct) => Record($"connect {network}");

        public Task<bool> OwnsAsync(string id, CancellationToken ct) => Task.FromResult(true);

        public Task<int> CountAsync(CancellationToken ct) => Task.FromResult(0);

        public Task<EngineResources> ListAsync(CancellationToken ct) => Task.FromResult(new EngineResources([], [], []));

        public Task PrepareWorkspaceAsync(WorkspaceSpec spec, CancellationToken ct)
        {
            Calls.Add("workspace");
            Workspace = spec;
            return FailWorkspace ? throw new ChargehandException(Chargehand.Contracts.ErrorCode.ContainerUnavailable, "no docker", "start it") : Task.CompletedTask;
        }

        public Task WriteOutFileAsync(OutFileSpec spec, ReadOnlyMemory<byte> content, CancellationToken ct)
        {
            Calls.Add($"write {spec.Name}");
            Bundle = content.ToArray();
            return Task.CompletedTask;
        }

        public Task<bool> ReadOutFileAsync(OutFileSpec spec, Stream destination, CancellationToken ct) => Task.FromResult(false);

        private Task Record(string call)
        {
            Calls.Add(call);
            return Task.CompletedTask;
        }
    }

    private static readonly string[] Argv = ["x"];

    private static string Verdict(int exit, string tail = "", bool timedOut = false) =>
        VerifyBranchCli.Marker + JsonSerializer.Serialize(new { argv = Argv, exit_code = exit, output_tail = tail, timed_out = timedOut, duration_ms = 4200, commit = "c" });

    private AcceptanceRequest Request(TimeSpan? timeout = null) =>
        new("t1", _repo, "chargehand/run-1", _base, ["sh", "-c", "test -f b.txt"], timeout ?? TimeSpan.FromMinutes(5), Image, ["registry.npmjs.org", "pypi.org"]);

    private static ContainerAcceptanceRunner Runner(FakeEngine engine) =>
        new(engine, engine, engine, new AcceptanceLimits(2048, 1.5, 256), TimeSpan.FromMilliseconds(1), TimeSpan.Zero);

    [Fact]
    public async Task A_passing_check_is_a_record_with_exit_zero_and_leaves_nothing_behind()
    {
        var engine = new FakeEngine(() => "noise\n" + Verdict(0, "all good") + "\n");
        var result = await Runner(engine).RunAsync(Request(), default);
        Assert.Equal(new AcceptanceResult(true, 0, false, 4.2, "all good"), result);

        // The network has one way out: the egress proxy with the allowlist.
        Assert.Equal(["registry.npmjs.org", "pypi.org"], engine.Egress!.Allow);
        Assert.Equal(Image, engine.Egress.Image);
        Assert.Equal(engine.Started!.Network, engine.Egress.Network);

        // The branch goes in as a bundle through the seed file of a fresh output volume.
        Assert.StartsWith("# v2 git bundle", Encoding.ASCII.GetString(engine.Bundle!, 0, 15));
        Assert.Contains("write seed.bundle", engine.Calls);
        Assert.Equal(("chargehand/run-1", _base, _repo), (engine.Workspace!.Branch, engine.Workspace.Commit, engine.Workspace.SourcePath));

        var spec = engine.Started;
        Assert.Equal(["verify-branch", "--bundle", "/out/seed.bundle", "--branch", "chargehand/run-1", "--timeout-seconds", "300", "--", "sh", "-c", "test -f b.txt"], spec.Command);
        Assert.Equal((Image, 2048, 1.5, 256), (spec.ImageDigest, spec.MemoryMb, spec.Cpus, spec.Pids));
        // Only the proxy is in the environment: no model credential, no run token.
        Assert.Equal(["HTTPS_PROXY", "HTTP_PROXY"], spec.Env.Keys.Order(StringComparer.Ordinal));
        Assert.All(spec.Env.Values, v => Assert.Contains("chargehand-egress-", v));

        Assert.Contains($"remove container-1", engine.Calls);
        Assert.Contains($"remove-volume {RunnerNames.Work(spec.RunId)}", engine.Calls);
        Assert.Contains($"remove-volume {RunnerNames.Out(spec.RunId)}", engine.Calls);
        Assert.Contains($"remove-network {spec.Network}", engine.Calls);
        Assert.Contains("remove egress-1", engine.Calls);
    }

    [Fact]
    public async Task A_failing_check_keeps_its_exit_code_and_the_tail_of_its_output()
    {
        var engine = new FakeEngine(() => Verdict(1, "AssertionError: x != y"));
        var result = await Runner(engine).RunAsync(Request(), default);
        Assert.False(result.Passed);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("AssertionError", result.OutputTail);
    }

    [Fact]
    public async Task A_check_that_timed_out_does_not_pass_even_with_exit_zero()
    {
        var result = await Runner(new FakeEngine(() => Verdict(0, "", timedOut: true))).RunAsync(Request(), default);
        Assert.False(result.Passed);
        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task A_container_that_ends_without_a_record_fails_with_its_logs_and_a_note_for_a_memory_kill()
    {
        var none = await Runner(new FakeEngine(() => "boom", () => new ContainerState(ContainerStatus.Exited, 3, false))).RunAsync(Request(), default);
        Assert.Equal((false, 3), (none.Passed, none.ExitCode));
        Assert.Contains("ended without a record", none.OutputTail);
        Assert.EndsWith("boom", none.OutputTail);
        var oom = await Runner(new FakeEngine(() => "", () => new ContainerState(ContainerStatus.Exited, 137, true))).RunAsync(Request(), default);
        Assert.Contains("ran out of memory", oom.OutputTail);
    }

    [Fact]
    public async Task A_forged_record_in_the_middle_of_the_output_is_not_the_verdict_unless_it_is_last()
    {
        // The record the verifier prints last is the one read; the tests' own output goes inside it, never beside it.
        var engine = new FakeEngine(() => Verdict(0, "forged") + "\n" + Verdict(1, "real"));
        Assert.False((await Runner(engine).RunAsync(Request(), default)).Passed);
    }

    [Fact]
    public async Task A_container_that_never_ends_is_killed_and_reported()
    {
        var engine = new FakeEngine(() => "", () => new ContainerState(ContainerStatus.Running, null, false));
        var result = await Runner(engine).RunAsync(Request(TimeSpan.FromMilliseconds(5)), default);
        Assert.False(result.Passed);
        Assert.True(result.TimedOut);
        Assert.Contains("signal SIGKILL", engine.Calls);
        Assert.Contains("remove container-1", engine.Calls);
    }

    [Fact]
    public async Task A_workspace_that_cannot_be_made_still_removes_what_was_made()
    {
        var engine = new FakeEngine(() => "") { FailWorkspace = true };
        await Assert.ThrowsAsync<ChargehandException>(() => Runner(engine).RunAsync(Request(), default));
        Assert.DoesNotContain("start", engine.Calls);
        Assert.Contains(engine.Calls, c => c.StartsWith("remove-network ", StringComparison.Ordinal));
        Assert.Contains(engine.Calls, c => c.StartsWith("remove-volume ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_branch_that_is_not_in_the_source_directory_is_an_error_before_any_container()
    {
        var engine = new FakeEngine(() => "");
        var request = Request() with { Branch = "chargehand/other" };
        await Assert.ThrowsAsync<BenchException>(() => Runner(engine).RunAsync(request, default));
        Assert.Empty(engine.Calls);
    }
}
