using Chargehand.Kernel;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Modules.Runners.Tests;

/// <summary>ADR 0078: the install helper runs on a network of its own whose egress reaches the toolchain hosts only, and leaves nothing behind.</summary>
public class MiseInstallerTests
{
    private const string Image = "registry.example/session-mise@sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string EgressImage = "registry.example/chargehand@sha256:2222222222222222222222222222222222222222222222222222222222222222";

    private sealed class FakeEngine : IContainerEngine
    {
        public List<string> Calls { get; } = [];
        public ContainerSpec? Started { get; private set; }
        public EgressSpec? Egress { get; private set; }
        public int Running { get; set; }
        public int ExitCode { get; set; }
        public string Logs { get; set; } = "";

        public Task CreateNetworkAsync(string name, string batchId, CancellationToken ct) { Calls.Add($"network {name}"); return Task.CompletedTask; }
        public Task RemoveNetworkAsync(string name, CancellationToken ct) { Calls.Add($"network-rm {name}"); return Task.CompletedTask; }
        public Task<string> StartEgressAsync(EgressSpec spec, CancellationToken ct) { Egress = spec; Calls.Add("egress"); return Task.FromResult("egress-id"); }
        public Task ConnectNetworkAsync(string container, string network, CancellationToken ct) { Calls.Add($"connect {network}"); return Task.CompletedTask; }
        public Task<string> StartAsync(ContainerSpec spec, CancellationToken ct) { Started = spec; Calls.Add("start"); return Task.FromResult("helper-id"); }
        public Task SignalAsync(string id, string signal, CancellationToken ct) { Calls.Add($"signal {signal}"); Running = 0; return Task.CompletedTask; }
        public Task<ContainerState> InspectAsync(string id, CancellationToken ct) =>
            Task.FromResult(Running-- > 0 ? new ContainerState(ContainerStatus.Running, null, false) : new ContainerState(ContainerStatus.Exited, ExitCode, false));
        public Task RemoveAsync(string id, CancellationToken ct) { Calls.Add($"rm {id}"); return Task.CompletedTask; }
        public Task<string> LogsTailAsync(string id, int bytes, CancellationToken ct) => Task.FromResult(Logs);
        public Task<EngineResources> ListAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task KillAllAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task CreateVolumeAsync(string name, string runId, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveVolumeAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> OwnsAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private static MiseInstall Request(TimeSpan? timeout = null) =>
        new("run1", Image, EgressImage, "chargehand-work-run1", "chargehand-out-run1", HomeVolumes.Name("o/r"), 2048, 2, 256, Timeout: timeout);

    [Fact]
    public async Task The_helper_gets_a_network_of_its_own_with_the_toolchain_hosts_and_both_are_removed_after()
    {
        var engine = new FakeEngine { Logs = $"installing\n{ContainerTemplate.ToolsMarker}\nnode 22.11.0 ~/x/.nvmrc\ndotnet 8.0.404 ~/x/mise.toml\n" };
        var result = await new MiseInstaller(engine, TimeSpan.FromMilliseconds(1)).RunAsync(Request(), default);

        Assert.Equal(["network chargehand-net-run1-install", "egress", "connect bridge", "start", "rm helper-id", "rm egress-id", "network-rm chargehand-net-run1-install"],
            engine.Calls.Where(c => !c.StartsWith("signal", StringComparison.Ordinal)).ToList());
        Assert.Equal(HomeVolumes.ToolchainHosts, engine.Egress!.Allow);
        Assert.DoesNotContain("api.anthropic.com", engine.Egress.Allow);
        var spec = engine.Started!;
        Assert.True(spec.Install);
        Assert.Equal("run1-install", spec.RunId);
        Assert.Equal("chargehand-net-run1-install", spec.Network);
        Assert.Equal(HomeVolumes.Name("o/r"), spec.Home);
        Assert.Equal(ContainerTemplate.InstallCommand, spec.Command);
        Assert.Equal(["HTTPS_PROXY", "HTTP_PROXY"], spec.Env.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(spec.Env.Keys, k => k.Contains("TOKEN") || k.Contains("KEY"));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["node 22.11.0 ~/x/.nvmrc", "dotnet 8.0.404 ~/x/mise.toml"], result.Tools);
        _ = ContainerTemplate.RunArgs(spec, "/dev/null");
    }

    [Fact]
    public async Task A_helper_that_fails_reports_its_exit_code_and_the_end_of_its_log_and_still_cleans_up()
    {
        var engine = new FakeEngine { ExitCode = 1, Logs = "mise ERROR no such version" };
        var result = await new MiseInstaller(engine, TimeSpan.FromMilliseconds(1)).RunAsync(Request(), default);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Tools);
        Assert.Contains("no such version", result.LogTail);
        Assert.Equal("network-rm chargehand-net-run1-install", engine.Calls[^1]);
    }

    [Fact]
    public async Task A_helper_that_outlives_its_time_is_killed()
    {
        var engine = new FakeEngine { Running = int.MaxValue };
        var result = await new MiseInstaller(engine, TimeSpan.FromMilliseconds(1)).RunAsync(Request(TimeSpan.FromMilliseconds(20)), default);
        Assert.True(result.TimedOut);
        Assert.Contains("signal SIGKILL", engine.Calls);
        Assert.Equal("network-rm chargehand-net-run1-install", engine.Calls[^1]);
    }

    [Fact]
    public async Task The_network_is_removed_when_the_helper_cannot_start()
    {
        var engine = new ThrowingStart();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MiseInstaller(engine, TimeSpan.FromMilliseconds(1)).RunAsync(Request(), default));
        Assert.Equal("network-rm chargehand-net-run1-install", engine.Calls[^1]);
    }

    private sealed class ThrowingStart : IContainerEngine
    {
        public List<string> Calls { get; } = [];
        public Task CreateNetworkAsync(string name, string batchId, CancellationToken ct) { Calls.Add($"network {name}"); return Task.CompletedTask; }
        public Task RemoveNetworkAsync(string name, CancellationToken ct) { Calls.Add($"network-rm {name}"); return Task.CompletedTask; }
        public Task<string> StartEgressAsync(EgressSpec spec, CancellationToken ct) => Task.FromResult("egress-id");
        public Task ConnectNetworkAsync(string container, string network, CancellationToken ct) => Task.CompletedTask;
        public Task<string> StartAsync(ContainerSpec spec, CancellationToken ct) => throw new InvalidOperationException("docker run failed");
        public Task RemoveAsync(string id, CancellationToken ct) { Calls.Add($"rm {id}"); return Task.CompletedTask; }
        public Task SignalAsync(string id, string signal, CancellationToken ct) => throw new NotSupportedException();
        public Task<ContainerState> InspectAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> LogsTailAsync(string id, int bytes, CancellationToken ct) => throw new NotSupportedException();
        public Task<EngineResources> ListAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task KillAllAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task CreateVolumeAsync(string name, string runId, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveVolumeAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> OwnsAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}

/// <summary>ADR 0078: the home volume's name is the cache volume's hash without the ecosystem, and nothing else is a home volume.</summary>
public class HomeVolumesTests
{
    [Fact]
    public void The_name_reuses_the_cache_volumes_hash_and_ignores_case_and_spelling_of_the_remote()
    {
        var cache = CacheVolumes.Name("nuget", "o/r");
        Assert.Equal(HomeVolumes.Name("o/r")[^12..], cache[^12..]);
        Assert.Equal(HomeVolumes.Name("o/r"), HomeVolumes.Name(" O/R "));
        Assert.NotEqual(HomeVolumes.Name("o/r"), HomeVolumes.Name("o/other"));
    }

    [Theory]
    [InlineData("chargehand-cache-0123456789ab", true)]
    [InlineData("chargehand-cache-nuget-0123456789ab", false)]
    [InlineData("chargehand-cache-0123456789AB", false)]
    [InlineData("chargehand-work-run1", false)]
    public void Only_that_shape_is_a_home_volume(string name, bool expected)
    {
        Assert.Equal(expected, HomeVolumes.IsHomeVolume(name));
        Assert.Null(CacheVolumes.EcosystemOf("chargehand-cache-0123456789ab")); // and it is not an ecosystem cache
    }

    [Fact]
    public void Every_toolchain_host_is_a_name_the_egress_proxy_accepts()
    {
        _ = new Chargehand.Modules.Gateway.Api.AllowlistMatcher(HomeVolumes.ToolchainHosts);
        Assert.DoesNotContain(HomeVolumes.ToolchainHosts, h => h.Contains("anthropic"));
    }
}
