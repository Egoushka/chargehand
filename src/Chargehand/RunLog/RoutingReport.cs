using System.Globalization;
using System.Text;

namespace Chargehand.RunLog;

/// <summary>
/// Routing suggestions from the run log, for review by hand (ADR 0019): per preset, node kind and model, the runs, their
/// score, prompt tokens, cache rate, cost and latency. Routing stays static per preset and node kind (decisions.md): a
/// cell keeps its default until it and an alternative each have <see cref="MinRuns"/> scored runs, and models never
/// switch within a node. The report only suggests; nothing reads it back.
/// </summary>
public static class RoutingReport
{
    public const int MinRuns = 20;

    /// <summary>An alternative is a candidate when it scores within this of the default...</summary>
    public const double QualityTolerance = 0.10;

    /// <summary>...and costs at least this share less.</summary>
    public const double CostSaving = 0.15;

    private sealed record Row(string Preset, string Kind, string Model, int Runs, int Completed, int Scored, double? Score, double PromptTokens, double? CacheRate,
        double Usd, double UsdP90, double LatencyP50, double LatencyP90);

    public static string Build(RunLogData data)
    {
        var scores = data.Scores.Where(s => s.Name == "quality").GroupBy(s => s.RunId).ToDictionary(g => g.Key, g => g.Average(s => s.Value));
        var calls = data.Calls.Where(c => c.Kind != "intake").GroupBy(c => c.RunId).ToDictionary(g => g.Key, g => g.ToList());
        var rows = data.Runs.Where(r => calls.ContainsKey(r.RunId))
            .GroupBy(r => (r.Preset, calls[r.RunId][0].Kind, Model: calls[r.RunId].GroupBy(c => c.Model).MaxBy(m => m.Count())!.Key))
            .Select(g =>
            {
                var runCalls = g.SelectMany(r => calls[r.RunId]).ToList();
                var prompt = runCalls.Sum(c => c.PromptTokens ?? 0);
                var scored = g.Where(r => scores.ContainsKey(r.RunId)).Select(r => scores[r.RunId]).ToList();
                var usd = g.Select(r => (double)r.Result.Usage.Usd).ToList();
                var seconds = g.Select(r => (r.Finished - r.Started).TotalSeconds).ToList();
                return new Row(g.Key.Preset, g.Key.Kind, g.Key.Model, g.Count(), g.Count(r => r.Result.Status == Contracts.ResultStatus.Completed), scored.Count,
                    scored.Count == 0 ? null : scored.Average(), (double)prompt / g.Count(), prompt == 0 ? null : (double)runCalls.Sum(c => c.Tokens?.CacheRead ?? 0) / prompt,
                    usd.Average(), Percentile(usd, 0.9), Percentile(seconds, 0.5), Percentile(seconds, 0.9));
            })
            .OrderBy(r => r.Preset, StringComparer.Ordinal).ThenBy(r => r.Kind, StringComparer.Ordinal).ThenByDescending(r => r.Runs).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("| preset | node kind | model | runs | completed | scored | score | prompt tokens/run | cache | usd/run | usd p90 | latency p50 | latency p90 | suggestion |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var cell in rows.GroupBy(r => (r.Preset, r.Kind)))
        {
            // The model with the most runs stands for the preset's default in this cell.
            var @default = cell.First();
            foreach (var r in cell)
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {r.Preset} | {r.Kind} | {r.Model} | {r.Runs} | {r.Completed} | {r.Scored} | {r.Score?.ToString("0.00", CultureInfo.InvariantCulture) ?? "—"} | {r.PromptTokens:0} | " +
                              $"{(r.CacheRate is { } c ? c.ToString("0%", CultureInfo.InvariantCulture) : "—")} | {r.Usd:0.0000} | {r.UsdP90:0.0000} | {r.LatencyP50:0}s | {r.LatencyP90:0}s | {Suggest(r, @default)} |");
        }
        sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Suggestions only: a model is a candidate once it and the default each have {MinRuns} scored runs, it scores within " +
                                   $"{QualityTolerance:0.00} of the default and costs at least {CostSaving:0%} less. Models never switch within a node.");
        return sb.ToString();
    }

    private static string Suggest(Row r, Row @default)
    {
        if (r.Scored < MinRuns || @default.Scored < MinRuns)
            return $"keep default ({Math.Min(r.Scored, @default.Scored)} scored < {MinRuns})";
        if (r == @default)
            return "default";
        return r.Score >= @default.Score - QualityTolerance && r.Usd <= @default.Usd * (1 - CostSaving)
            ? string.Create(CultureInfo.InvariantCulture, $"candidate: score {r.Score - @default.Score:+0.00;-0.00}, cost {r.Usd / @default.Usd - 1:+0%;-0%}")
            : "keep default";
    }

    private static double Percentile(IReadOnlyList<double> values, double q)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[Math.Max(0, (int)Math.Ceiling(q * sorted.Count) - 1)];
    }
}
