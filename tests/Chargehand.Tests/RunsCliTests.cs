using Chargehand.Containers;
using Chargehand.Contracts;


namespace Chargehand.Tests;

public class RunsCliTests
{
    private sealed class Engine(int count, Exception? failure = null) : IContainerEngine
    {
        public bool Killed { get; private set; }
        public Task<int> CountAsync(CancellationToken ct) => Task.FromResult(count);
        public Task KillAllAsync(CancellationToken ct) { Killed = true; return failure is null ? Task.CompletedTask : throw failure; }
        public Task<string> StartAsync(ContainerSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public Task SignalAsync(string id, string signal, CancellationToken ct) => throw new NotSupportedException();
        public Task<ContainerState> InspectAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> LogsTailAsync(string id, int bytes, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateVolumeAsync(string name, string runId, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveVolumeAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateNetworkAsync(string name, string batchId, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveNetworkAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> StartEgressAsync(EgressSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public Task ConnectNetworkAsync(string container, string network, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> OwnsAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Kill_all_removes_by_label_and_says_how_many_without_a_server()
    {
        var engine = new Engine(3);
        var output = new StringWriter();
        Assert.Equal(0, await RunsCli.KillAllAsync(engine, output, TextWriter.Null, default));
        Assert.True(engine.Killed);
        Assert.Contains("every session container", output.ToString());
    }

    [Fact]
    public async Task An_engine_that_is_down_is_exit_1_with_the_action()
    {
        var error = new StringWriter();
        var down = new ChargehandException(ErrorCode.ContainerUnavailable, "docker ps failed", "Check that the Docker engine is running.");
        Assert.Equal(1, await RunsCli.KillAllAsync(new Engine(0, down), TextWriter.Null, error, default));
        Assert.Contains("Docker engine is running", error.ToString());
    }
}
