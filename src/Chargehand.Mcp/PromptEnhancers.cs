using Chargehand.Config;
using Chargehand.Enhancement;

namespace Chargehand.Mcp;

/// <summary>Builds the prompt enhancer of a profile (ADR 0040): the one place a client gets it from.</summary>
public static class PromptEnhancers
{
    /// <summary>The profile's <c>prompt_enhancer</c> behind a <see cref="GuardedPromptEnhancer"/>, or null when it sets none. <paramref name="time"/> is the deadline's clock.</summary>
    public static IPromptEnhancer? From(Profile profile, McpConnectionPool pool, TimeProvider? time = null) =>
        profile.PromptEnhancer is not { } settings
            ? null
            : new GuardedPromptEnhancer(new McpPromptEnhancer(settings, pool), TimeSpan.FromMilliseconds(settings.EffectiveDeadlineMs), time);
}
