using Chargehand.Runtime;

namespace Chargehand.Budget;

/// <summary>Prices token counts with the orchestrator's own table, cache-write surcharge included (ADR 0011).</summary>
public interface IPriceTable
{
    decimal PriceUsd(string model, TokenCounts tokens);
}

/// <summary>USD per million tokens.</summary>
public sealed record ModelPrice(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite);

/// <summary>
/// OpenCode reports output without reasoning (gateway completion = output + reasoning), and input without
/// cache reads and writes; each class is billed at its own rate.
/// </summary>
public sealed class PriceTable(IReadOnlyDictionary<string, ModelPrice> prices) : IPriceTable
{
    public decimal PriceUsd(string model, TokenCounts t)
    {
        var p = prices.TryGetValue(model, out var price) ? price : throw new KeyNotFoundException($"no price for model '{model}' in the profile");
        return (t.Input * p.Input + (t.Output + t.Reasoning) * p.Output + t.CacheRead * p.CacheRead + t.CacheWrite * p.CacheWrite) / 1_000_000m;
    }
}
