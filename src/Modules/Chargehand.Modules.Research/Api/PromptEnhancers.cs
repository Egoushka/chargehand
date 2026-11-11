using Chargehand.Modules.Memory.Api;

namespace Chargehand.Modules.Research.Api;

/// <summary>Builds the prompt enhancer of a profile (ADR 0040): the one place a client gets it from.</summary>
public static class PromptEnhancers
{
    /// <summary>The profile's <c>prompt_enhancer</c> (<paramref name="settings"/>) behind a <see cref="GuardedPromptEnhancer"/>, or null when it sets none. <paramref name="time"/> is the deadline's clock.</summary>
    public static IPromptEnhancer? From(PromptEnhancerSettings? settings, McpConnectionPool pool, TimeProvider? time = null) =>
        settings is null
            ? null
            : new GuardedPromptEnhancer(new McpPromptEnhancer(settings, pool), TimeSpan.FromMilliseconds(settings.EffectiveDeadlineMs), time);
}
