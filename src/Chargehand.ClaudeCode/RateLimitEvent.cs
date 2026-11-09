using System.Text.Json;
using Chargehand.Contracts;

namespace Chargehand.ClaudeCode;

/// <summary>
/// Reads the subscription's usage windows from a stream-json <c>rate_limit_event</c> (ADR 0098). The CLI (2.1.283 and later) emits one when a window's rounded
/// percentage or reset time moves, built from the <c>anthropic-ratelimit-unified-*</c> response headers: <c>rate_limit_info.unifiedWindows.five_hour</c> and
/// <c>.seven_day</c> carry <c>utilization</c> (a fraction of the window) and <c>resetsAt</c> (epoch seconds). A session on an API key, Bedrock or Vertex gets no such event.
/// <c>unifiedWindows</c> is marked internal in the CLI's schema, so the top-level <c>rateLimitType</c>, <c>utilization</c> and <c>resetsAt</c> (the window that is limiting
/// now) are the fallback, and only for the two windows this reads.
/// </summary>
internal static class RateLimitEvent
{
    /// <summary>The windows the event reports, or null when it reports none (another window, no utilization, a value that is not a number).</summary>
    public static SubscriptionUsage? Read(JsonElement e)
    {
        if (!e.TryGetProperty("rate_limit_info", out var info) || info.ValueKind != JsonValueKind.Object)
            return null;
        if (info.TryGetProperty("unifiedWindows", out var windows) && windows.ValueKind == JsonValueKind.Object
            && SubscriptionUsage.Of(Window(windows, "five_hour"), Window(windows, "seven_day")) is { } both)
            return both;
        var type = info.TryGetProperty("rateLimitType", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var limiting = Window(info, null);
        return type switch
        {
            SubscriptionUsage.FiveHourName => SubscriptionUsage.Of(limiting, null),
            SubscriptionUsage.SevenDayName => SubscriptionUsage.Of(null, limiting),
            _ => null,
        };
    }

    /// <summary>One window: the object under <paramref name="name"/>, or the object itself when <paramref name="name"/> is null. Null without a finite, non-negative utilization.</summary>
    private static UsageWindow? Window(JsonElement parent, string? name)
    {
        var w = parent;
        if (name is not null && (!parent.TryGetProperty(name, out w) || w.ValueKind != JsonValueKind.Object))
            return null;
        if (!w.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number || !u.TryGetDouble(out var used) || !double.IsFinite(used) || used < 0)
            return null;
        DateTimeOffset? resets = null;
        // A reset that does not fit a date is dropped, not the window: the share is still a reading.
        if (w.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var seconds) && seconds is > 0 and < 253_402_300_799)
            resets = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return new UsageWindow(used, resets);
    }
}
