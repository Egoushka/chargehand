using Chargehand.Runtime;

namespace Chargehand.Budget;

/// <summary>Prices token counts with the orchestrator's own table, cache-write surcharge included (ADR 0011).</summary>
public interface IPriceTable
{
    /// <summary>Null: no price entry for <paramref name="model"/> (ADR 0026) — unknown cost, not free.</summary>
    decimal? PriceUsd(string model, TokenCounts tokens);
}

/// <summary>USD per million tokens.</summary>
public sealed record ModelPrice(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite);

/// <summary>
/// OpenCode reports output without reasoning (gateway completion = output + reasoning), and input without
/// cache reads and writes; each class is billed at its own rate.
/// </summary>
public sealed class PriceTable(IReadOnlyDictionary<string, ModelPrice> prices) : IPriceTable
{
    public decimal? PriceUsd(string model, TokenCounts tokens)
    {
        if (!prices.TryGetValue(model, out var p))
            return null;
        return (tokens.Input * p.Input + (tokens.Output + tokens.Reasoning) * p.Output + tokens.CacheRead * p.CacheRead + tokens.CacheWrite * p.CacheWrite) / 1_000_000m;
    }
}
