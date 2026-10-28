namespace Chargehand.Runtimes.Acp.Api;

/// <summary>
/// The profile's <c>acp</c> block (ADR 0086, decisions 8 to 10). A request names an agent with <c>runtime: "acp:&lt;id&gt;"</c>; the id must be in the
/// pinned <c>acp-agents.json</c>. <see cref="Agents"/> says what pays for each agent's calls: an API key or a gateway's key, always by
/// secret item name. There is no setting for a subscription login, and the agent never sees the user's home directory, so none can be found.
/// </summary>
/// <param name="AllowUncapped">Run an agent that reports no usage under a dollar cap anyway. Unset, such a run is refused <c>runtime_uncapped</c>:
/// its spend could not be counted, so the cap could not hold.</param>
/// <param name="AgentsFile">The pinned list, when it is not <c>acp-agents.json</c> in the chargehand root.</param>
/// <param name="Agents">Credentials by agent id.</param>
public sealed record AcpSettings(bool AllowUncapped = false, string? AgentsFile = null, IReadOnlyDictionary<string, AcpAgentCredential>? Agents = null)
{
    public AcpAgentCredential? CredentialFor(string agentId) => Agents is not null && Agents.TryGetValue(agentId, out var credential) ? credential : null;
}

/// <param name="ApiKeySecret">The secret item that holds the key: the provider's, or a gateway's virtual key. Never the key itself.</param>
/// <param name="BaseUrl">A gateway that speaks the agent's provider API; unset, the agent calls the provider directly.</param>
public sealed record AcpAgentCredential(string? ApiKeySecret = null, string? BaseUrl = null);
