using System.Text.Json;

namespace AgentClientProtocol;

/// <summary>
/// A client for one agent: the other end of an ACP connection (protocol version 1). Start an agent with <see cref="Launch"/>
/// or wrap two streams with <see cref="Connect"/>, call <see cref="InitializeAsync"/>, then open sessions.
/// An agent that dies fails every request in flight with <see cref="AcpConnectionException"/>.
/// </summary>
public sealed partial class AcpClient : IAsyncDisposable
{
    /// <summary>The protocol version this library speaks.</summary>
    public const int ProtocolVersion = 1;

    private readonly AcpClientOptions _options;
    private readonly JsonRpcConnection _connection;
    private readonly AgentProcess? _process;

    private AcpClient(Stream fromAgent, Stream toAgent, AcpClientOptions options, AgentProcess? process)
    {
        _options = options;
        _process = process;
        _connection = new JsonRpcConnection(fromAgent, toAgent, new Incoming(this), options.OnWire, options.OnError,
            ct => process is null
                ? Task.FromResult<Exception>(new AcpConnectionException("the agent closed the connection"))
                : process.DescribeExitAsync(ct).ContinueWith(t => (Exception)t.Result, TaskScheduler.Default));
        _connection.Start();
    }

    /// <summary>What the agent reported at <c>initialize</c>; null before then.</summary>
    public InitializeResult? Agent { get; private set; }

    /// <summary>The last lines the agent wrote to standard error; empty when it was not started by <see cref="Launch"/>.</summary>
    public string StderrTail => _process?.StderrTail ?? "";

    /// <summary>Completes with the reason when the connection ends.</summary>
    public Task<Exception> Closed => _connection.Closed;

    /// <summary>Starts <paramref name="launch"/> as a child process and speaks ACP over its standard input and output.</summary>
    /// <exception cref="AcpConnectionException">The process cannot start.</exception>
    public static AcpClient Launch(AcpLaunchOptions launch, AcpClientOptions options)
    {
        var process = AgentProcess.Start(launch);
        return new AcpClient(process.FromAgent, process.ToAgent, options, process);
    }

    /// <summary>Speaks ACP over two streams: <paramref name="fromAgent"/> carries the agent's output, <paramref name="toAgent"/> its input.
    /// The client takes ownership of both.</summary>
    public static AcpClient Connect(Stream fromAgent, Stream toAgent, AcpClientOptions options) => new(fromAgent, toAgent, options, null);

    /// <summary>Negotiates the protocol version and exchanges capabilities. The client offers only what its callbacks can answer.</summary>
    /// <exception cref="AcpProtocolException">The agent answers with a version other than <see cref="ProtocolVersion"/>.</exception>
    public async Task<InitializeResult> InitializeAsync(CancellationToken ct = default)
    {
        var request = new InitializeRequest(ProtocolVersion, OfferedCapabilities(), _options.ClientInfo);
        var result = await _connection.RequestAsync("initialize", request, AcpJsonContext.Default.InitializeRequest, AcpJsonContext.Default.InitializeResult, ct).ConfigureAwait(false);
        if (result.ProtocolVersion != ProtocolVersion)
            throw new AcpProtocolException($"the agent speaks ACP version {result.ProtocolVersion}; this client speaks version {ProtocolVersion}");
        Agent = result;
        return result;
    }

    /// <summary>Signs in with one of the methods <see cref="InitializeResult.AuthMethods"/> lists.</summary>
    public async Task AuthenticateAsync(string methodId, CancellationToken ct = default) =>
        await _connection.RequestAsync("authenticate", new AuthenticateRequest(methodId), AcpJsonContext.Default.AuthenticateRequest, AcpJsonContext.Default.EmptyResult, ct).ConfigureAwait(false);

    /// <summary>Opens a session working in <paramref name="cwd"/> (an absolute path), with the MCP servers the agent should connect to.</summary>
    public async Task<AcpSession> NewSessionAsync(string cwd, IReadOnlyList<McpServer>? mcpServers = null, CancellationToken ct = default)
    {
        var result = await _connection.RequestAsync("session/new", new NewSessionRequest(cwd, mcpServers ?? []),
            AcpJsonContext.Default.NewSessionRequest, AcpJsonContext.Default.NewSessionResult, ct).ConfigureAwait(false);
        return new AcpSession(this, result.SessionId, result.Modes, result.Models, result.ConfigOptions ?? []);
    }

    /// <summary>Reopens a session the agent kept. The agent replays its history as updates first; they reach <see cref="AcpClientOptions.OnSessionUpdate"/>.
    /// Needs <see cref="AgentCapabilities.LoadSession"/>.</summary>
    public async Task<AcpSession> LoadSessionAsync(string sessionId, string cwd, IReadOnlyList<McpServer>? mcpServers = null, CancellationToken ct = default)
    {
        var result = await _connection.RequestAsync("session/load", new LoadSessionRequest(sessionId, cwd, mcpServers ?? []),
            AcpJsonContext.Default.LoadSessionRequest, AcpJsonContext.Default.LoadSessionResult, ct).ConfigureAwait(false);
        return new AcpSession(this, sessionId, result.Modes, result.Models, result.ConfigOptions ?? []);
    }

    /// <summary>Stops the agent: closes its input, waits for it to exit, and kills its process tree if it does not.</summary>
    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        if (_process is not null)
            await _process.DisposeAsync().ConfigureAwait(false);
    }

    internal TimeSpan CancelGrace => _options.CancelGrace;

    internal Task<TResult> CallAsync<TParams, TResult>(
        string method, TParams value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams> paramsInfo,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultInfo, CancellationToken ct) =>
        _connection.RequestAsync(method, value, paramsInfo, resultInfo, ct);

    internal Task NotifyAsync<TParams>(string method, TParams value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams> paramsInfo) =>
        _connection.NotifyAsync(method, value, paramsInfo, CancellationToken.None);

    private ClientCapabilities OfferedCapabilities() => new(
        _options.OnReadTextFile is null && _options.OnWriteTextFile is null
            ? null
            : new FileSystemCapabilities(_options.OnReadTextFile is not null, _options.OnWriteTextFile is not null),
        _options.Terminal is null ? null : true);
}
