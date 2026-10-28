using Chargehand.Acp;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Runtime;
using Chargehand.Runtimes.Acp.Api;
using Chargehand.Sandbox;

namespace Chargehand.Tests;

/// <summary>An ACP runtime over the scripted fake agent (tests/AgentClientProtocol.FakeAgent), started the way a real agent is: a child process on stdio.</summary>
internal static class AcpTestSupport
{
    public static AcpAgentSpec Fake(bool reportsUsage = true, string? authMethod = "fake-key", string? readOnlyMode = null, string command = "") => new(
        "fake", "Fake agent", command.Length > 0 ? command : DotnetHost(), [Path.Combine(AppContext.BaseDirectory, "AgentClientProtocol.FakeAgent.dll")], "1.0.0",
        ApiKeyEnv: "FAKE_API_KEY", AuthMethod: authMethod, ReadOnlyMode: readOnlyMode, ReportsUsage: reportsUsage, Provider: "fakeco");

    public static AcpWorkerRuntime NewRuntime(AcpAgentSpec? agent = null, ISandbox? sandbox = null) =>
        new(agent ?? Fake(), new Dictionary<string, string> { ["FAKE_API_KEY"] = "k-test" }, sandbox ?? new NoSandbox());

    public static NodeSpec Spec(string directory, params ServiceGrant[] services) => new(directory, "research", null, [],
        new Dictionary<string, string> { [IRunCleanup.RunMetadataKey] = "run-1" }, services);

    public static async Task<(string Id, IReadOnlyList<WorkerMessage> Messages, IdleOutcome Outcome)> Turn(AcpWorkerRuntime runtime, string directory, string prompt)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var session = await runtime.CreateAsync(Spec(directory), deadline.Token);
        await runtime.SubmitAsync(session.Id, prompt, deadline.Token);
        var outcome = await runtime.AwaitIdleAsync(session.Id, deadline.Token);
        return (session.Id, await runtime.ReadMessagesAsync(session.Id, deadline.Token), outcome);
    }

    private static string DotnetHost() => Environment.ProcessPath is { } p && Path.GetFileNameWithoutExtension(p) == "dotnet" ? p : "dotnet";
}

/// <summary>A runtime, a checkout directory and the run's end: disposing stops the agent processes the test started.</summary>
internal sealed class AcpFixture(AcpAgentSpec? agent = null, ISandbox? sandbox = null) : IAsyncDisposable
{
    public TempDir Dir { get; } = new();

    public AcpWorkerRuntime Runtime { get; } = AcpTestSupport.NewRuntime(agent, sandbox);

    public Task<(string Id, IReadOnlyList<WorkerMessage> Messages, IdleOutcome Outcome)> Turn(string prompt) => AcpTestSupport.Turn(Runtime, Dir.Path, prompt);

    public async ValueTask DisposeAsync()
    {
        await Runtime.EndRunAsync("run-1", default);
        Dir.Dispose();
    }
}

/// <summary>Records what the runtime asked the sandbox for, then runs it with no confinement.</summary>
internal sealed class RecordingSandbox : ISandbox
{
    private readonly NoSandbox _inner = new();

    public List<SandboxSpec> Wrapped { get; } = [];

    public string Kind => "recording";

    public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken ct) => _inner.RunAsync(spec, ct);

    public SandboxLaunch Wrap(SandboxSpec spec)
    {
        Wrapped.Add(spec);
        return _inner.Wrap(spec);
    }
}
