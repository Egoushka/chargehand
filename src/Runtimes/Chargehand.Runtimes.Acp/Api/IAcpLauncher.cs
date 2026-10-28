using AgentClientProtocol;

namespace Chargehand.Runtimes.Acp.Api;

/// <summary>What an agent's callbacks reach. A permission request waits in <paramref name="OnPermission"/> for as long as its answer takes.</summary>
public sealed record AcpAgentCallbacks(Func<SessionNotification, ValueTask> OnUpdate, Func<RequestPermissionRequest, ValueTask<RequestPermissionOutcome>> OnPermission);

/// <summary>One running agent with its ACP session open. Disposing stops the process.</summary>
public interface IAcpAgent : IAsyncDisposable
{
    AcpSession Session { get; }

    /// <summary>The model the session runs, as the agent names it; null when it does not say.</summary>
    string? Model { get; }

    /// <summary>The mode id set to make the agent read-only; null when it lists none.</summary>
    string? ReadOnlyMode { get; }

    /// <summary>Completes with the reason when the agent's connection ends, whoever ended it.</summary>
    Task<Exception> Closed { get; }

    string StderrTail { get; }
}

/// <summary>Starts an agent process the way a host wants it confined, and opens its session (ADR 0086, decision 8). The seam between the runtime and the sandbox, which lives in the host's core.</summary>
public interface IAcpLauncher
{
    /// <param name="environment">What <see cref="AcpLaunchEnvironment.Build"/> made: the key and the gateway.</param>
    /// <param name="directory">The checkout the session works in.</param>
    /// <param name="model">The model id to switch to; null: the agent's own default.</param>
    /// <exception cref="Chargehand.Kernel.ChargehandException">The agent could not start, or does not offer the model.</exception>
    Task<IAcpAgent> StartAsync(AcpAgentSpec agent, IReadOnlyDictionary<string, string> environment, string directory, string? model, AcpAgentCallbacks callbacks, CancellationToken ct);
}
