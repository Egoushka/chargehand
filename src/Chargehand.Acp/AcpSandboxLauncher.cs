using Chargehand.Runtime;
using Chargehand.Runtimes.Acp.Api;
using Chargehand.Sandbox;

namespace Chargehand.Acp;

/// <summary>Starts an agent as <see cref="AcpConnection"/> does for a research worker: inside the profile's sandbox, with a private home, the key in its environment, the checkout read-only
/// and the agent's own read-only mode set.</summary>
public sealed class AcpSandboxLauncher(ISandbox sandbox) : IAcpLauncher
{
    public async Task<IAcpAgent> StartAsync(AcpAgentSpec agent, IReadOnlyDictionary<string, string> environment, string directory, string? model, AcpAgentCallbacks callbacks, CancellationToken ct) =>
        await AcpConnection.StartAsync(agent, environment, sandbox, directory, model is null ? null : new ModelRef(agent.ProviderId, model), callbacks, ct);
}
