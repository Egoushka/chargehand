using Chargehand.Runtime;

namespace Chargehand.Budget;

/// <summary>Prices token counts with the orchestrator's own table, cache-write surcharge included (ADR 0011).</summary>
public interface IPriceTable
{
    decimal PriceUsd(string model, TokenCounts tokens);
}

/// <summary>USD per million tokens.</summary>
public sealed record ModelPrice(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite);
