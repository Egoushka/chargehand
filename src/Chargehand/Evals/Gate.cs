using System.Globalization;
using System.Text.RegularExpressions;

namespace Chargehand.Evals;

/// <summary>One item's paired runs: quality 0–1 and cost of base and change. Null cost (ADR 0026): the arm's model
/// had no price entry; it carries no cost ratio and contributes nothing to the total (not zeroed-and-counted).</summary>
public sealed record Pair(string ItemId, double BaseQuality, double ChangeQuality, decimal? BaseUsd, decimal? ChangeUsd);

/// <summary>A trade declared in the pull request: quality may fall to <paramref name="Quality"/> if cost changes by at most <paramref name="Cost"/>.</summary>
/// <param name="Quality">Lowest mean paired quality change accepted, e.g. -0.15.</param>
/// <param name="Cost">Highest relative cost change accepted, e.g. -0.25: cost must fall at least 25%.</param>
public sealed record Trade(double Quality, double Cost);

/// <param name="QualityDelta">Mean of change minus base quality over the items.</param>
/// <param name="CostChange">Total change cost over total base cost, minus 1.</param>
public sealed record Verdict(string Cell, int Items, double QualityDelta, double? QualityT, double CostChange, double? CostT, bool Blocked, string Reason);

/// <summary>
/// Prompt CI's decision for one cell (ADR 0019). Without a trade, a change is blocked when mean quality falls by more
/// than T and a one-sided paired t-test puts the drop below chance (p &lt; 0.05), or when cost rises by more than C
/// with the same test on log cost ratios: noise alone does not block, and neither does a real change inside the
/// tolerances. A declared trade replaces T and C with its bounds, which the measured means must meet.
/// </summary>
public static partial class Gate
{
    /// <summary>The commit status text: blocked cells, else files no cell gates, else what ran and what passed without a
    /// cell (allowed or new), never "no prompt or preset change" when there was one.</summary>
    public static string Describe(IReadOnlyList<Verdict> verdicts, IReadOnlyList<string> uncovered, IReadOnlyList<string> fresh, bool allowUncovered)
    {
        var blocked = verdicts.Where(v => v.Blocked).ToList();
        if (blocked.Count > 0)
            return string.Join("; ", blocked.Select(v => $"{v.Cell}: {v.Reason}"));
        if (uncovered.Count > 0 && !allowUncovered)
            return $"no eval cell gates {string.Join(", ", uncovered)}";
        var parts = verdicts.Select(v => $"{v.Cell}: {v.Reason}").ToList();
        if (uncovered.Count > 0)
            parts.Add($"allowed without an eval cell: {string.Join(", ", uncovered)}");
        if (fresh.Count > 0)
            parts.Add($"new, gated from the next change by its cell: {string.Join(", ", fresh)}");
        return parts.Count == 0 ? "no prompt or preset change" : string.Join("; ", parts);
    }

    public static Verdict Decide(EvalCell cell, IReadOnlyList<Pair> pairs, Trade? trade)
    {
        var d = pairs.Select(p => p.ChangeQuality - p.BaseQuality).ToList();
        var dq = d.Count == 0 ? 0 : d.Average();
        var tq = T(d);
        // Sum() over a nullable sequence never returns null (unpriced pairs, ADR 0026, just contribute nothing).
        var sumBase = pairs.Sum(p => p.BaseUsd).GetValueOrDefault();
        var cost = sumBase == 0 ? 0 : (double)(pairs.Sum(p => p.ChangeUsd).GetValueOrDefault() / sumBase) - 1;
        // Items where either arm cost nothing or unpriced carry no cost ratio.
        var tc = T([.. pairs.Where(p => p.BaseUsd > 0 && p.ChangeUsd > 0).Select(p => Math.Log((double)(p.ChangeUsd!.Value / p.BaseUsd!.Value)))]);
        Verdict V(bool blocked, FormattableString reason) => new(cell.Name, pairs.Count, dq, tq, cost, tc, blocked, FormattableString.Invariant(reason));

        if (pairs.Count < cell.MinItems)
            return V(true, $"{pairs.Count} items; the gate needs at least {cell.MinItems}");
        var critical = Critical(pairs.Count - 1);
        if (trade is not null)
        {
            if (dq < trade.Quality)
                return V(true, $"trade not delivered: quality {dq:+0.000;-0.000} below the declared {trade.Quality:+0.00;-0.00}");
            return cost > trade.Cost
                ? V(true, $"trade not delivered: cost {cost:+0%;-0%} above the declared {trade.Cost:+0%;-0%}")
                : V(false, $"declared trade holds: quality {dq:+0.000;-0.000}, cost {cost:+0%;-0%}");
        }
        if (dq < -cell.QualityTolerance && tq < -critical)
            return V(true, $"quality {dq:+0.000;-0.000} beyond T {cell.QualityTolerance:0.00} (t {tq:0.00} < -{critical:0.00})");
        if (cost > cell.CostTolerance && tc > Critical(Math.Max(1, pairs.Count(p => p.BaseUsd > 0 && p.ChangeUsd > 0) - 1)))
            return V(true, $"cost {cost:+0%;-0%} beyond C {cell.CostTolerance:+0%} (t {tc:0.00})");
        return V(false, $"quality {dq:+0.000;-0.000} (T {cell.QualityTolerance:0.00}), cost {cost:+0%;-0%} (C {cell.CostTolerance:+0%})");
    }

    /// <summary>The trade line from a pull request body: "prompt-ci: trade quality>=-0.15 cost<=-25%".</summary>
    public static Trade? ParseTrade(string? body) =>
        TradeLine().Match(body ?? "") is { Success: true } m
            ? new Trade(double.Parse(m.Groups["q"].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups["c"].Value, CultureInfo.InvariantCulture) / 100)
            : null;

    /// <summary>Paired t statistic of the differences; ±∞ when they are all equal and nonzero; null below 2.</summary>
    internal static double? T(IReadOnlyList<double> d)
    {
        if (d.Count < 2)
            return null;
        var mean = d.Average();
        var sd = Math.Sqrt(d.Sum(x => (x - mean) * (x - mean)) / (d.Count - 1));
        return sd == 0 ? (mean == 0 ? 0 : mean * double.PositiveInfinity) : mean / (sd / Math.Sqrt(d.Count));
    }

    /// <summary>One-sided 95% critical value of Student's t.</summary>
    internal static double Critical(int df) => df < 1 ? double.PositiveInfinity : df <= Table.Length ? Table[df - 1] : 1.645;

    private static readonly double[] Table =
    [
        6.314, 2.920, 2.353, 2.132, 2.015, 1.943, 1.895, 1.860, 1.833, 1.812, 1.796, 1.782, 1.771, 1.761, 1.753,
        1.746, 1.740, 1.734, 1.729, 1.725, 1.721, 1.717, 1.714, 1.711, 1.708, 1.706, 1.703, 1.701, 1.699, 1.697,
    ];

    [GeneratedRegex(@"prompt-ci:\s*trade\s+quality\s*>=\s*(?<q>-?\d+(?:\.\d+)?)\s+cost\s*<=\s*(?<c>[-+]?\d+(?:\.\d+)?)%", RegexOptions.IgnoreCase)]
    private static partial Regex TradeLine();
}
