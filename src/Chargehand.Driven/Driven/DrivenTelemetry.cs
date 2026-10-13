using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Budget;
using Chargehand.Runtime;

namespace Chargehand.Driven;

/// <summary>The spans of a driven batch (ADR 0042), on the same source and in the same Langfuse project as an orchestrator run: <c>chargehand.driven.run</c> for the batch,
/// <c>chargehand.driven.task</c> per task, and under it <c>chargehand.driven.session</c>, the Claude Code session as one generation. The batch's trace id is the one every
/// <c>result/v1</c> of the batch carries.</summary>
public static partial class DrivenTelemetry
{
    public const string RunSpan = "chargehand.driven.run";
    public const string TaskSpan = "chargehand.driven.task";
    public const string SessionSpan = "chargehand.driven.session";

    /// <summary>Tags the session span. With <paramref name="usageOnSpans"/> it carries the session's tokens and the price table's cost, as a call span does (ADR 0021):
    /// a subscription session reaches Anthropic with no gateway to record it.</summary>
    public static void TagSession(Activity? span, SessionOutcome session, IReadOnlyDictionary<string, ModelPrice> prices, bool usageOnSpans)
    {
        if (span is null)
            return;
        span.SetTag("gen_ai.request.model", session.Model);
        span.SetTag("chargehand.claude_code.session_id", session.SessionId);
        span.SetTag("chargehand.session.status", JsonNamingPolicy.SnakeCaseLower.ConvertName(session.Status.ToString()));
        span.SetTag("chargehand.session.reason", session.Reason.Length == 0 ? null : session.Reason);
        span.SetTag("chargehand.session.turns", session.Turns);
        if (session.ModelUsage is { Count: > 1 } models)
            span.SetTag("chargehand.session.models", models.Keys.Order(StringComparer.Ordinal).ToArray());
        if (!usageOnSpans)
            return;
        var t = Total(session);
        span.SetTag("langfuse.observation.usage_details", JsonSerializer.Serialize(new Dictionary<string, long>
        {
            ["input"] = t.Input,
            ["output"] = t.Output,
            ["cache_read_input_tokens"] = t.CacheRead,
            ["cache_creation_input_tokens"] = t.CacheWrite,
        }));
        // Unknown cost (ADR 0026) reports no cost_details rather than a $0 that would read as measured.
        if (PriceUsd(prices, session) is { } usd)
            span.SetTag("langfuse.observation.cost_details", JsonSerializer.Serialize(new Dictionary<string, decimal> { ["total"] = usd }));
        // Claude Code's own list-price figure, beside the table's, for a check; on a subscription neither is a bill.
        span.SetTag("chargehand.claude_code.cost_usd", session.CostUsd);
    }

    /// <summary>The session's tokens: the sum over its models when the stream gave them, else the result's totals.</summary>
    public static TokenCounts Total(SessionOutcome session) =>
        session.ModelUsage is { Count: > 0 } models
            ? new TokenCounts(models.Values.Sum(m => m.Input), models.Values.Sum(m => m.Output), 0, models.Values.Sum(m => m.CacheRead), models.Values.Sum(m => m.CacheWrite))
            : new TokenCounts(session.InputTokens, session.OutputTokens, 0, session.CacheReadTokens, session.CacheWriteTokens);

    /// <summary>The session priced with the profile's table, model by model; null when any model it used has no price (ADR 0026).</summary>
    public static decimal? PriceUsd(IReadOnlyDictionary<string, ModelPrice> prices, SessionOutcome session)
    {
        IEnumerable<(string Model, TokenCounts Tokens)> parts = session.ModelUsage is { Count: > 0 } models
            ? models.Select(m => (m.Key, m.Value))
            : session.Model is { } started ? [(started, Total(session))] : [];
        decimal total = 0;
        var any = false;
        foreach (var (model, tokens) in parts)
        {
            if (Find(prices, model) is not { } key || new PriceTable(prices).PriceUsd(key, tokens) is not { } usd)
                return null;
            total += usd;
            any = true;
        }
        return any ? total : null;
    }

    /// <summary>The table's key for a model id as Claude Code names it (<c>claude-sonnet-5-5</c>): the key itself, else the one key whose part after the provider is that id,
    /// with or without a date suffix. Keys under several providers count only when they agree on the price.</summary>
    public static string? Find(IReadOnlyDictionary<string, ModelPrice> prices, string model)
    {
        if (prices.ContainsKey(model))
            return model;
        foreach (var id in new[] { model, DateSuffix().Replace(model, "") }.Distinct())
        {
            var keys = prices.Keys.Where(k => k.EndsWith($"/{id}", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
            if (keys.Count > 0)
                return keys.Select(k => prices[k]).Distinct().Count() == 1 ? keys[0] : null;
        }
        return null;
    }

    [GeneratedRegex(@"-\d{8}$")]
    private static partial Regex DateSuffix();
}
