using Chargehand.Contracts;

namespace Chargehand.Modules.Ledger.Api;

/// <summary>The coarse size intake's own token guess buckets into (issue #427): intake no longer guesses dollars, so this is the
/// only size signal a cost estimate has to key its calibration on.</summary>
public static class EstimateSizeClass
{
    public const string Small = "S";
    public const string Medium = "M";
    public const string Large = "L";

    /// <summary>Buckets a token count the way the driven governor's own S/M/L classes read (small single-file edits, a typical task,
    /// a multi-file one): thresholds are a starting point, not tuned against real traffic yet.</summary>
    public static string Of(long tokens) => tokens <= 20_000 ? Small : tokens <= 100_000 ? Medium : Large;
}

/// <summary>One finished run, reduced to what calibration needs (issue #427): what the price table alone would have said at the
/// dispatched model's rate for intake's own token guess, against what the run actually cost. <see cref="Ratio"/> is null for a
/// sample the price table could not price (no entry for the model, or a zero-token guess) — it carries no information either way.</summary>
public sealed record EstimateSample(string Preset, string Size, decimal NaiveUsd, decimal ActualUsd)
{
    public decimal? Ratio => NaiveUsd == 0 ? null : ActualUsd / NaiveUsd;
}

/// <summary>Builds <see cref="EstimateSample"/>s from the run log's own records (issue #427): a finished run whose spec carries
/// intake's token guess, whose result carries a priced usage, and whose route names the model that actually ran it.</summary>
public static class EstimateHistory
{
    public static IReadOnlyList<EstimateSample> From(IReadOnlyList<RunRecord> runs, IPriceTable prices)
    {
        List<EstimateSample> samples = [];
        foreach (var run in runs)
        {
            if (run.Spec is not { } spec || run.Result.Usage.Usd is not { } actual)
                continue;
            if (run.Route?.Model is not { } model)
                continue;
            var rate = prices.AveragePriceUsdPerToken(model);
            if (rate is not { } r)
                continue;
            var naive = spec.Estimate.TokensHigh * r;
            if (naive <= 0)
                continue;
            samples.Add(new(run.Preset, EstimateSizeClass.Of(spec.Estimate.TokensHigh), naive, actual));
        }
        return samples;
    }
}

/// <summary>Corrects the price table's naive token-times-rate figure for whatever it still gets wrong (a single-shot price ignores
/// retries, multi-turn sessions, tool calls): a per (preset, size) multiplier, the median of <see cref="EstimateSample.Ratio"/> over
/// the run log's own history, so applying it to a fresh estimate keeps that median near 1 (issue #427).</summary>
public static class EstimateCalibration
{
    /// <summary>Below this many same-bucket samples, the multiplier is too noisy to trust: the fallback (1, the naive price
    /// unchanged) is used instead.</summary>
    public const int MinSamples = 5;

    public static decimal Factor(IReadOnlyList<EstimateSample> history, string preset, string size, decimal fallback = 1m)
    {
        var ratios = history.Where(s => s.Preset == preset && s.Size == size).Select(s => s.Ratio).Where(r => r is not null).Select(r => r!.Value).Order().ToList();
        return ratios.Count >= MinSamples ? Median(ratios) : fallback;
    }

    /// <summary><paramref name="sorted"/> ascending; the midpoint of the two middle values on an even count.</summary>
    private static decimal Median(List<decimal> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
}

/// <summary>Computes task-spec/v1's <c>estimate</c> in code instead of trusting intake's own guess at it (issue #427): intake, which
/// runs on a small model, kept inventing a USD range priced as if it were the model running the work, when the preset dispatches a
/// different (usually larger) one. Intake's token guess is kept as the estimate's effort signal; the dollar range comes from the
/// dispatched model's own price, times a calibration factor learned from the run log per preset and size.</summary>
public static class CostEstimator
{
    public static Estimate Compute(Estimate intakeGuess, string preset, string dispatchedModel, IPriceTable prices, IReadOnlyList<EstimateSample> history)
    {
        var size = EstimateSizeClass.Of(intakeGuess.TokensHigh);
        var rate = prices.AveragePriceUsdPerToken(dispatchedModel);
        if (rate is not { } r)
            return intakeGuess with { Basis = $"intake's own token guess kept; no price entry for dispatched model {dispatchedModel} (issue #427)" };
        var factor = EstimateCalibration.Factor(history, preset, size);
        var low = Math.Round(intakeGuess.TokensLow * r * factor, 4);
        var high = Math.Round(intakeGuess.TokensHigh * r * factor, 4);
        return intakeGuess with { UsdLow = low, UsdHigh = high, Basis = $"{dispatchedModel} price x{factor:0.##} calibration (preset {preset}, size {size})" };
    }
}
