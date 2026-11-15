using Chargehand.Kernel;

namespace Chargehand.Modules.Ledger.Api;

/// <summary>Prices token counts with the orchestrator's own table, cache-write surcharge included (ADR 0011).</summary>
public interface IPriceTable
{
    /// <summary>Null: no price entry for <paramref name="model"/> (ADR 0026) — unknown cost, not free.</summary>
    decimal? PriceUsd(string model, TokenCounts tokens);

    /// <summary>A blended USD-per-token rate for <paramref name="model"/>, input and output averaged since an estimate (issue #427) has no input/output split
    /// to price separately: the mean of <see cref="ModelPrice.Input"/> and <see cref="ModelPrice.Output"/>, per token rather than per million. Null: no price
    /// entry for <paramref name="model"/>.</summary>
    decimal? AveragePriceUsdPerToken(string model);
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

    public decimal? AveragePriceUsdPerToken(string model) =>
        prices.TryGetValue(model, out var p) ? (p.Input + p.Output) / 2 / 1_000_000m : null;
}
