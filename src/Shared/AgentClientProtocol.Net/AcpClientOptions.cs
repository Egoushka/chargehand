namespace AgentClientProtocol;

/// <summary>Which way a line travelled, for <see cref="AcpClientOptions.OnWire"/>.</summary>
public enum AcpWireDirection
{
    /// <summary>Written to the agent.</summary>
    ToAgent,

    /// <summary>Read from the agent. A line that is not JSON-RPC is passed on too.</summary>
    FromAgent,
}

/// <summary>Where the client's callbacks for terminal methods go. Setting <see cref="AcpClientOptions.Terminal"/> offers the agent a terminal.</summary>
public interface IAcpTerminalHandler
{
    ValueTask<CreateTerminalResponse> CreateAsync(CreateTerminalRequest request, CancellationToken cancellationToken);

    ValueTask<TerminalOutputResponse> OutputAsync(TerminalRequest request, CancellationToken cancellationToken);

    ValueTask<TerminalExitStatus> WaitForExitAsync(TerminalRequest request, CancellationToken cancellationToken);

    ValueTask KillAsync(TerminalRequest request, CancellationToken cancellationToken);

    ValueTask ReleaseAsync(TerminalRequest request, CancellationToken cancellationToken);
}

/// <summary>What the client does when the agent calls it. A method with no callback is declined, and the client does not offer it at <c>initialize</c>.</summary>
public sealed class AcpClientOptions
{
    /// <summary>How this client names itself to the agent.</summary>
    public Implementation ClientInfo { get; init; } = new("agentclientprotocol-net");

    /// <summary>Called for each <c>session/update</c>, in the order the agent sent them and before the prompt they belong to returns.
    /// A slow callback delays reading the next line, so hand heavy work off.</summary>
    public Func<SessionNotification, CancellationToken, ValueTask>? OnSessionUpdate { get; init; }

    /// <summary>Answers <c>session/request_permission</c>. With no callback every request is answered <c>cancelled</c>, which an agent treats as a refusal.</summary>
    public Func<RequestPermissionRequest, CancellationToken, ValueTask<RequestPermissionOutcome>>? OnRequestPermission { get; init; }

    /// <summary>Answers <c>fs/read_text_file</c>. Set to offer the agent file reads.</summary>
    public Func<ReadTextFileRequest, CancellationToken, ValueTask<ReadTextFileResponse>>? OnReadTextFile { get; init; }

    /// <summary>Answers <c>fs/write_text_file</c>. Set to offer the agent file writes.</summary>
    public Func<WriteTextFileRequest, CancellationToken, ValueTask>? OnWriteTextFile { get; init; }

    /// <summary>Answers the <c>terminal/*</c> methods. Set to offer the agent terminals.</summary>
    public IAcpTerminalHandler? Terminal { get; init; }

    /// <summary>Called with every line written to or read from the agent: for transcripts and debugging. It runs on the reader or writer, so keep it quick.</summary>
    public Action<AcpWireDirection, string>? OnWire { get; init; }

    /// <summary>Called when a callback above throws. The client declines the call and carries on.</summary>
    public Action<Exception>? OnError { get; init; }

    /// <summary>How long a cancelled prompt waits for the agent to end the turn before it gives up. Default 10 seconds.</summary>
    public TimeSpan CancelGrace { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>How to start an agent as a child process that speaks ACP on its standard input and output.</summary>
public sealed class AcpLaunchOptions
{
    public required string FileName { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    /// <summary>Variables to set for the agent; a null value removes one.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    /// <summary>Start the agent with only <see cref="Environment"/>, not this process's variables. The caller then names PATH and HOME itself.</summary>
    public bool ClearEnvironment { get; init; }

    /// <summary>How long disposing waits for the agent to exit after its input closes, before it kills the process tree. Default 2 seconds.</summary>
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Called with each line the agent writes to standard error.</summary>
    public Action<string>? OnStderr { get; init; }
}
