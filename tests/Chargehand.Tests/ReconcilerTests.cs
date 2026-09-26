using Chargehand.Contracts;
using Chargehand.RunLog;
using Chargehand.Runtime;

namespace Chargehand.Tests;

public class ReconcilerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static CallRecord Call(int second, long cacheWrite, long output, decimal usd) => new(
        "run-1", "worker", "worker", "ses_1", $"msg_{second}", "p/gpt-x", T0.AddSeconds(second), 1000,
        new TokenCounts(3, output, 10, 5000, cacheWrite), usd, new PromptChain([], new AsSent("2.0.16", "build", "p/gpt-x", "2026-09-26")));

    [Fact]
    public void Joins_on_model_tokens_and_time_and_uses_each_row_once()
    {
        CallRecord[] calls = [Call(0, 200, 90, 0.010m), Call(30, 200, 90, 0.011m)];
        SpendRow[] rows =
        [
            new("openai/gpt-x", 5203, 100, T0.AddSeconds(31), 0.0112m),
            new("openai/gpt-x", 5203, 100, T0.AddSeconds(1), 0.0101m),
            new("openai/other", 5203, 100, T0.AddSeconds(0), 9m),
        ];
        var r = Reconciler.Match(calls, rows);
        Assert.Equal(0, r.Unmatched);
        Assert.Equal(0.0101m, r.Matches[0].Row!.Spend);
        Assert.Equal(0.0112m, r.Matches[1].Row!.Spend);
        Assert.Equal(0.0213m, r.GatewayUsd);
        Assert.Equal(0.021m, r.OwnUsd);
    }

    [Fact]
    public void Gateway_start_lagging_inside_a_long_call_still_matches()
    {
        // OpenCode created the message at 0 s; the call took 33 s; the gateway logged its start at 30 s.
        var call = Call(0, 200, 90, 0.01m) with { LatencyMs = 33_000 };
        var r = Reconciler.Match([call], [new("gpt-x", 5203, 100, T0.AddSeconds(30), 0.01m)]);
        Assert.Equal(0, r.Unmatched);
    }

    [Fact]
    public void Rows_outside_the_window_or_with_other_counts_stay_unmatched()
    {
        var r = Reconciler.Match([Call(0, 200, 90, 0.01m)], [new("gpt-x", 5203, 100, T0.AddSeconds(60), 0.01m), new("gpt-x", 5204, 100, T0, 0.01m)]);
        Assert.Equal(1, r.Unmatched);
    }
}
