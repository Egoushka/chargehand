namespace Chargehand.Runtimes.Acp.Api;

/// <summary>One agent of the pinned list <c>acp-agents.json</c> (ADR 0086, decision 8): how to start it and what is known about it.</summary>
/// <param name="Id">What a request names after <c>acp:</c>.</param>
/// <param name="Command">The program to start; with <see cref="Args"/> its package carries <see cref="Version"/>, so a run never starts "latest".</param>
/// <param name="Sha256">The archive hash, where the ACP registry gives one (it does not for npm packages).</param>
/// <param name="ApiKeyEnv">The variable the agent reads its API key from. Every agent has one: a run without a key is refused.</param>
/// <param name="BaseUrlEnv">The variable that points the agent at a gateway; null: the agent cannot be pointed at one.</param>
/// <param name="AuthMethod">The <c>authenticate</c> method that takes the key from the environment; null: the agent needs no call.</param>
/// <param name="ReadOnlyMode">The agent's own read-only or plan mode; null: found by name (<see cref="AcpModes"/>).</param>
/// <param name="ReportsUsage">Whether the agent reports token usage with its turns. False: a dollar cap cannot hold on it (decision 10).</param>
/// <param name="Env">Fixed variables the agent is started with.</param>
/// <param name="Provider">The provider part of the <c>provider/model</c> label the run log and the profile's price table use; null: the agent's id.</param>
public sealed record AcpAgentSpec(
    string Id,
    string Name,
    string Command,
    IReadOnlyList<string> Args,
    string Version,
    string ApiKeyEnv,
    string? Sha256 = null,
    string? BaseUrlEnv = null,
    string? AuthMethod = null,
    string? ReadOnlyMode = null,
    bool ReportsUsage = false,
    IReadOnlyDictionary<string, string>? Env = null,
    string? Provider = null)
{
    public string ProviderId => Provider ?? Id;
}
