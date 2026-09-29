using Chargehand.Config;
using Chargehand.Memory;

namespace Chargehand.Mcp;

/// <summary>Builds the memory stack of a profile (ADR 0034): the one place the CLI's <c>run</c>, <c>serve</c> and <c>mcp</c> commands, and so the server, get it from.</summary>
public static class MemoryStacks
{
    /// <summary>
    /// The profile's <c>memory</c> as a stack, or null when it lists nothing. Each list entry becomes one source over the shared
    /// <paramref name="pool"/>, in list order, with the entry's limits, retain flag and tags, in the scope of its namespace
    /// (its name when it sets none). The old single object, which the profile never holds beside the list, becomes its one
    /// source through <paramref name="objectForm"/>; both go when the object form does.
    /// </summary>
    /// <param name="objectForm">Builds the provider of the single object (the Hindsight HTTP client).</param>
    public static MemoryStack? From(Profile profile, McpConnectionPool pool, Func<MemorySettings, IMemoryProvider> objectForm)
    {
        if (profile.Memory is not { } memory)
            return null;
        if (memory.ObjectForm is { } settings)
            return MemoryStack.ForObjectForm(settings, objectForm(settings));
        return memory.Providers.Count == 0
            ? null
            : new MemoryStack([.. memory.Providers.Select(p =>
                new MemorySource(p.Name, new McpMemoryProvider(p, pool), new MemoryScope(p.Name, p.EffectiveNamespace), p.Limits, p.Retain, p.EffectiveRetainTags))]);
    }
}
