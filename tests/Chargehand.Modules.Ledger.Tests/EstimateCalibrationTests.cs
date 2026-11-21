using Chargehand.Contracts;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Modules.Ledger.Tests;

/// <summary>Issue #427: intake's own dollar guess correlated poorly with actual cost because it priced the small intake model,
/// not the (usually larger) one the preset dispatches. <see cref="CostEstimator"/> prices the dispatched model's rate instead,
/// calibrated against the run log's own history so the predicted/actual ratio stays near 1.</summary>
public class EstimateCalibrationTests
{
    private const string Model = "worker/large";
    private static readonly PriceTable Prices = new(new Dictionary<string, ModelPrice>
    {
        [Model] = new(1.0m, 3.0m, 0.1m, 0.5m), // average 2.0 usd / 1e6 tokens
    });

    private static TaskSpec Spec(long tokensHigh) =>
        new("task-spec/v1", "t", "goal", [], [], Risk.Low, new Estimate(tokensHigh / 2, tokensHigh, 0m, 0m, "intake guess"), TaskAction.Answer, null);

    private static ResultContract Result(decimal usd) =>
        new("result/v1", "r", "run", new string('0', 32), new PromptChain([], new AsSent("", "", "", "2026-01-01")), ResultStatus.Completed, "done", [], [], [], [], 1,
            new Usage(0, 0, 0, 0, usd));

    private static RunRecord Run(string preset, long tokensHigh, decimal actualUsd) =>
        new("run", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, preset, null, Spec(tokensHigh), Result(actualUsd), Route: new RunRoute("pool", "tier", Model));

    [Fact]
    public void A_fixture_run_log_s_history_calibrates_a_fresh_estimate_s_median_ratio_near_one()
    {
        // The naive price-table figure for 10,000 tokens at the model's average rate: 10,000 * 2.0 / 1e6 = 0.02.
        // Actual runs came in consistently around 1.5x that (the issue's reported median), with some spread.
        decimal[] actualOverNaive = [1.3m, 1.4m, 1.5m, 1.6m, 1.7m];
        var runs = actualOverNaive.Select(ratio => Run("driven", 10_000, 0.02m * ratio)).ToList();

        var history = EstimateHistory.From(runs, Prices);
        Assert.Equal(5, history.Count);

        var fresh = CostEstimator.Compute(new Estimate(5_000, 10_000, 0m, 0m, "intake guess"), "driven", Model, Prices, history);

        // The calibration factor is the history's median ratio (1.5): applying it to the same token guess should land close to
        // what those very runs actually cost, i.e. the predicted/actual ratio stays near 1 rather than off by 1.5x as it was
        // with the naive price alone.
        var predictedOverActual = runs.Select(r => fresh.UsdHigh / r.Result.Usage.Usd!.Value).OrderBy(r => r).ToList();
        var median = predictedOverActual[predictedOverActual.Count / 2];
        Assert.InRange(median, 0.9m, 1.1m);
        Assert.Contains("x1.5", fresh.Basis);
    }

    [Fact]
    public void Fewer_than_five_samples_falls_back_to_the_naive_price_unchanged()
    {
        var history = EstimateHistory.From([Run("driven", 10_000, 0.03m), Run("driven", 10_000, 0.03m)], Prices);

        var estimate = CostEstimator.Compute(new Estimate(5_000, 10_000, 0m, 0m, "intake guess"), "driven", Model, Prices, history);

        // No calibration applied (factor 1): the naive price-table figure, 10,000 tokens at 2.0 usd/1e6.
        Assert.Equal(0.02m, estimate.UsdHigh);
    }

    [Fact]
    public void A_dispatched_model_with_no_price_entry_keeps_intake_s_own_guess()
    {
        var estimate = CostEstimator.Compute(new Estimate(5_000, 10_000, 1m, 2m, "intake guess"), "driven", "unknown/model", Prices, []);

        Assert.Equal(1m, estimate.UsdLow);
        Assert.Equal(2m, estimate.UsdHigh);
        Assert.Contains("no price entry", estimate.Basis);
    }

    [Fact]
    public void A_different_preset_or_size_bucket_does_not_share_another_s_calibration()
    {
        var runs = Enumerable.Range(0, 6).Select(_ => Run("driven", 10_000, 0.06m)).ToList(); // x3 for (driven, S)
        var history = EstimateHistory.From(runs, Prices);

        var otherPreset = CostEstimator.Compute(new Estimate(5_000, 10_000, 0m, 0m, "g"), "cheap", Model, Prices, history);
        Assert.Equal(0.02m, otherPreset.UsdHigh); // falls back to naive: no "cheap" samples

        var otherSize = CostEstimator.Compute(new Estimate(60_000, 120_000, 0m, 0m, "g"), "driven", Model, Prices, history);
        Assert.Equal(120_000m * 2.0m / 1_000_000m, otherSize.UsdHigh); // falls back to naive: no "L" samples
    }
}
