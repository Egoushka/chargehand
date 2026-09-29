using Chargehand.Config;
using Chargehand.Memory;

namespace Chargehand.Mcp;

/// <summary>Builds the memory stack of a profile (ADR 0034): the one place the CLI's <c>run</c>, <c>serve</c> and <c>mcp</c> commands, and so the server, get it from.</summary>
public static class MemoryStacks
{
    /// <summary>
    /// The profile's <c>memory</c> as a stack, or null when it lists nothing. Each list entry becomes one source over the shared
    /// <paramref name="pool"/>, in list order, with the entry's limits, retain flag and tags, in the scope of its namespace
    /// (its name when it sets none).
    /// </summary>
    public static MemoryStack? From(Profile profile, McpConnectionPool pool) =>
        profile.Memory is not { Count: > 0 } memory
            ? null
            : new MemoryStack([.. memory.Select(p =>
                new MemorySource(p.Name, new McpMemoryProvider(p, pool), new MemoryScope(p.Name, p.EffectiveNamespace), p.Limits, p.Retain, p.EffectiveRetainTags))]);
}
