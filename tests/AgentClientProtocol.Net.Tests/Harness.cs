using System.Collections.Concurrent;

namespace AgentClientProtocol.Tests;

/// <summary>An <see cref="AcpClient"/> on the scripted fake agent, with what the agent sent kept for the test to read.</summary>
internal sealed class Harness : IAsyncDisposable
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private readonly ConcurrentQueue<SessionNotification> _updates = new();
    private readonly ConcurrentQueue<(AcpWireDirection Direction, string Line)> _wire = new();
    private readonly SemaphoreSlim _arrived = new(0);

    private Harness(Func<Harness, AcpClientOptions>? options, params string[] agentArgs)
    {
        var configured = options?.Invoke(this) ?? new AcpClientOptions();
        Client = AcpClient.Launch(
            new AcpLaunchOptions
            {
                FileName = DotnetHost(),
                Arguments = [Path.Combine(AppContext.BaseDirectory, "AgentClientProtocol.FakeAgent.dll"), .. agentArgs],
                ShutdownGrace = TimeSpan.FromMilliseconds(500),
            },
            new AcpClientOptions
            {
                ClientInfo = configured.ClientInfo,
                OnSessionUpdate = async (n, ct) =>
                {
                    _updates.Enqueue(n);
                    _arrived.Release();
                    if (configured.OnSessionUpdate is { } inner)
                        await inner(n, ct);
                },
                OnRequestPermission = configured.OnRequestPermission,
                OnReadTextFile = configured.OnReadTextFile,
                OnWriteTextFile = configured.OnWriteTextFile,
                Terminal = configured.Terminal,
                OnWire = (d, l) => _wire.Enqueue((d, l)),
                CancelGrace = configured.CancelGrace,
            });
    }

    public AcpClient Client { get; }

    public IReadOnlyList<SessionNotification> Updates => [.. _updates];

    public IReadOnlyList<(AcpWireDirection Direction, string Line)> Wire => [.. _wire];

    /// <summary>The text of each agent message chunk so far, in arrival order.</summary>
    public IReadOnlyList<string> Chunks => [.. _updates.Select(n => n.Update).OfType<AgentMessageChunk>().Select(c => c.Content.Text ?? "")];

    public static Harness Start(Func<Harness, AcpClientOptions>? options = null, params string[] agentArgs) => new(options, agentArgs);

    /// <summary>Initializes and opens a session in a temp-like working directory.</summary>
    public async Task<AcpSession> NewSessionAsync()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await Client.InitializeAsync(cts.Token);
        return await Client.NewSessionAsync(Path.GetTempPath(), ct: cts.Token);
    }

    /// <summary>Waits until an update matching <paramref name="match"/> has arrived.</summary>
    public async Task WaitForUpdateAsync(Func<SessionUpdate, bool> match)
    {
        using var cts = new CancellationTokenSource(Deadline);
        while (!_updates.Any(n => match(n.Update)))
            await _arrived.WaitAsync(cts.Token);
    }

    public ValueTask DisposeAsync() => Client.DisposeAsync();

    private static string DotnetHost()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } fromBuild)
            return fromBuild;
        return Environment.ProcessPath is { } own && Path.GetFileNameWithoutExtension(own) == "dotnet" ? own : "dotnet";
    }
}
