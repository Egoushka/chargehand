using Chargehand.Budget;
using Chargehand.Runtime;

namespace Chargehand.Tests;

public class PriceTableTests
{
    private static readonly PriceTable Table = new(new Dictionary<string, ModelPrice>
    {
        ["p/small"] = new(0.10m, 0.50m, 0.01m, 0.125m),
    });

    [Fact]
    public void Prices_every_token_class_including_reasoning_and_cache_writes()
    {
        // 1M of each class: input 0.10 + (output+reasoning) 2 x 0.50 + read 0.01 + write 0.125
        var usd = Table.PriceUsd("p/small", new TokenCounts(1_000_000, 1_000_000, 1_000_000, 1_000_000, 1_000_000));
        Assert.Equal(1.235m, usd);
    }

    [Fact]
    public void Matches_a_gateway_billed_call()
    {
        // Spike call: gateway billed 0.0007123 for 5,616 written + 3 input + 20 completion tokens.
        Assert.Equal(0.0007123m, Table.PriceUsd("p/small", new TokenCounts(3, 20, 0, 0, 5616)), 7);
    }

    [Fact]
    public void Unknown_model_is_an_error_not_free() =>
        Assert.Throws<KeyNotFoundException>(() => Table.PriceUsd("p/unknown", new TokenCounts(1, 1, 0, 0, 0)));
}
