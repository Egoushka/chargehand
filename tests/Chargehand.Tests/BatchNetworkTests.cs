using Chargehand.Containers;

namespace Chargehand.Tests;

/// <summary>The per-batch internal network and its egress container (ADR 0039), against a recording fake engine.</summary>
public class BatchNetworkTests
{
    private const string Image = "registry.example/chargehand@sha256:0000000000000000000000000000000000000000000000000000000000000000";

    private sealed class RecordingEngine : IContainerEngine
    {
        public List<string> Calls { get; } = [];
        public EgressSpec? Egress { get; private set; }
        public string? FailOn { get; init; }

        private Task Do(string call)
        {
            Calls.Add(call);
            return FailOn == call.Split(' ')[0] ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        }

        public Task CreateNetworkAsync(string name, string batchId, CancellationToken ct) => Do($"network-create {name} {batchId}");
        public Task RemoveNetworkAsync(string name, CancellationToken ct) => Do($"network-rm {name}");
        public async Task<string> StartEgressAsync(EgressSpec spec, CancellationToken ct) { Egress = spec; await Do($"egress-start {spec.Network}"); return "egress-id-1"; }
        public Task ConnectNetworkAsync(string container, string network, CancellationToken ct) => Do($"connect {container} {network}");
        public Task RemoveAsync(string id, CancellationToken ct) => Do($"rm {id}");
        public Task<string> StartAsync(ContainerSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public Task SignalAsync(string id, string signal, CancellationToken ct) => throw new NotSupportedException();
        public Task<ContainerState> InspectAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task KillAllAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string> LogsTailAsync(string id, int bytes, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateVolumeAsync(string name, string runId, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveVolumeAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> OwnsAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Create_makes_the_internal_network_starts_egress_on_it_then_joins_the_outside_network()
    {
        var engine = new RecordingEngine();
        var info = await new BatchNetwork(engine).CreateAsync("b1", Image, ["api.anthropic.com", "*.nuget.org"], default);
        Assert.Equal(["network-create chargehand-net-b1 b1", "egress-start chargehand-net-b1", "connect egress-id-1 bridge"], engine.Calls);
        Assert.Equal("chargehand-net-b1", info.Network);
        Assert.Equal("chargehand-egress-b1", info.ProxyHost);
        Assert.Equal("http://run-7:x@chargehand-egress-b1:3128", info.ProxyUrl("run-7"));
        Assert.Equal(["api.anthropic.com", "*.nuget.org"], engine.Egress!.Allow);
    }

    [Fact]
    public async Task A_failure_part_way_removes_what_was_made()
    {
        var engine = new RecordingEngine { FailOn = "connect" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BatchNetwork(engine).CreateAsync("b1", Image, ["api.anthropic.com"], default));
        Assert.Equal(["rm egress-id-1", "network-rm chargehand-net-b1"], engine.Calls.Skip(3));
    }

    [Fact]
    public async Task Remove_removes_the_egress_container_then_the_network()
    {
        var engine = new RecordingEngine();
        var batch = new BatchNetwork(engine);
        var info = await batch.CreateAsync("b1", Image, ["api.anthropic.com"], default);
        engine.Calls.Clear();
        await batch.RemoveAsync(info, default);
        Assert.Equal(["rm egress-id-1", "network-rm chargehand-net-b1"], engine.Calls);
    }

    [Fact]
    public async Task A_bad_batch_id_or_allowlist_is_refused_before_anything_is_created()
    {
        var engine = new RecordingEngine();
        var batch = new BatchNetwork(engine);
        await Assert.ThrowsAsync<ArgumentException>(() => batch.CreateAsync("--x", Image, ["api.anthropic.com"], default));
        await Assert.ThrowsAsync<ArgumentException>(() => batch.CreateAsync("b1", Image, ["*"], default));
        await Assert.ThrowsAsync<ArgumentException>(() => batch.CreateAsync("b1", Image, [], default));
        Assert.Empty(engine.Calls);
    }

    [Fact]
    public void The_egress_line_is_fixed_and_carries_only_the_allowlist()
    {
        var args = ContainerTemplate.EgressArgs(new EgressSpec("b1", Image, "chargehand-net-b1", ["api.anthropic.com", "*.nuget.org"]));
        Assert.Equal(["run", "--detach", "--init"], args.Take(3));
        foreach (var flag in new[] { "--read-only", "--cap-drop", "--security-opt", "--user", "--pids-limit", "--memory", "--network" })
            Assert.Contains(flag, args);
        Assert.DoesNotContain("--privileged", args);
        Assert.DoesNotContain("-v", args);
        Assert.Equal("chargehand-net-b1", args[args.ToList().IndexOf("--network") + 1]);
        var image = args.ToList().IndexOf(Image);
        Assert.Equal(["egress", "--listen", "0.0.0.0:3128", "--allow", "api.anthropic.com,*.nuget.org"], args.Skip(image + 1));
    }

    [Theory]
    [InlineData("--x", "chargehand-net-b1")]
    [InlineData("b1", "/etc")]
    public void The_egress_line_refuses_odd_names(string batch, string network) =>
        Assert.Throws<ArgumentException>(() => ContainerTemplate.EgressArgs(new EgressSpec(batch, Image, network, ["api.anthropic.com"])));

    [Fact]
    public void A_local_image_id_is_accepted_as_content_addressed()
    {
        var id = "sha256:" + new string('a', 64);
        Assert.Contains(id, ContainerTemplate.EgressArgs(new EgressSpec("b1", id, "chargehand-net-b1", ["api.anthropic.com"])));
    }
}
