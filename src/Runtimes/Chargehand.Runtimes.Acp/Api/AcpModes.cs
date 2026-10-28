using AgentClientProtocol;

namespace Chargehand.Runtimes.Acp.Api;

/// <summary>The second read-only guard (ADR 0086, decision 8): the agent's own plan or read-only mode, set when it has one.</summary>
public static class AcpModes
{
    // Codex calls it read-only; Gemini CLI and the Claude adapter, plan.
    private static readonly string[] Known = ["read-only", "readonly", "read_only", "plan"];

    /// <param name="pinned">The mode the pinned list names for the agent; used when the agent lists it.</param>
    /// <returns>The mode id to set, as the agent spells it; null when the agent has no such mode.</returns>
    public static string? ReadOnly(SessionModeState? modes, string? pinned)
    {
        var available = modes?.AvailableModes?.Select(m => m.Id).ToList() ?? [];
        foreach (var wanted in pinned is null ? Known : [pinned, .. Known])
            if (available.FirstOrDefault(id => string.Equals(id, wanted, StringComparison.OrdinalIgnoreCase)) is { } found)
                return found;
        return null;
    }
}
