using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Runtime;
using Chargehand.Verification;

namespace Chargehand.Evals;

/// <summary>A labelled claim and the text it cites (<c>evals/support-examples.jsonl</c>); <c>Group</c> claims share one judge call, as a node's do.</summary>
public sealed record SupportExample(string Group, string Claim, string CitedText, string Expected);

/// <param name="Got">Null: the judge returned no valid verdicts for the group.</param>
public sealed record SupportOutcome(SupportExample Example, SupportVerdict? Got, string Reason);

/// <summary>Measures the support judge against labelled examples (ADR 0036, spec decision 12): agreement per expected class and every miss.</summary>
public static class SupportEval
{
    public static IReadOnlyList<SupportExample> Load(string path) =>
        [.. File.ReadLines(path).Where(l => l.Trim().Length > 0).Select(l => JsonSerializer.Deserialize<SupportExample>(l, ContractJson.Options)!)];

    public static async Task<IReadOnlyList<SupportOutcome>> RunAsync(IWorkerRuntime runtime, ModelRef? model, IReadOnlyList<SupportExample> examples, CancellationToken ct)
    {
        var outcomes = new List<SupportOutcome>();
        foreach (var group in examples.GroupBy(e => e.Group))
        {
            var members = group.ToList();
            var claims = members.Select((e, i) => new CitedClaim(i, new Claim(e.Claim, ["e1"], 1), $"[e1] file example\n{e.CitedText}", false)).ToList();
            try
            {
                var verdicts = await SupportJudge.JudgeAsync(runtime, model, claims, ct);
                outcomes.AddRange(members.Select((e, i) => new SupportOutcome(e, verdicts.Single(v => v.Index == i).Verdict, verdicts.Single(v => v.Index == i).Reason)));
            }
            catch (InvalidOperationException e)
            {
                outcomes.AddRange(members.Select(m => new SupportOutcome(m, null, e.Message)));
            }
        }
        return outcomes;
    }

    /// <summary>Agreement overall and per expected class, then one line per miss.</summary>
    public static string Report(IReadOnlyList<SupportOutcome> outcomes)
    {
        var lines = new List<string>();
        bool Hit(SupportOutcome o) => o.Got is { } g && string.Equals(g.ToString(), o.Example.Expected, StringComparison.OrdinalIgnoreCase);
        lines.Add($"agreement {outcomes.Count(Hit)}/{outcomes.Count}");
        foreach (var cls in outcomes.GroupBy(o => o.Example.Expected).OrderBy(g => g.Key, StringComparer.Ordinal))
            lines.Add($"  expected {cls.Key}: {cls.Count(Hit)}/{cls.Count()}");
        // The cost that matters: a claim the check would drop that was in fact supported, or keep that the text does not support.
        var droppedSupported = outcomes.Count(o => o.Example.Expected == "supported" && o.Got == SupportVerdict.Unsupported);
        var keptUnsupported = outcomes.Count(o => o.Example.Expected == "unsupported" && o.Got is SupportVerdict.Supported);
        lines.Add($"supported claims that would be dropped: {droppedSupported}; unsupported claims kept as supported: {keptUnsupported}");
        foreach (var o in outcomes.Where(o => !Hit(o)))
            lines.Add($"MISS [{o.Example.Group}] expected {o.Example.Expected}, got {o.Got?.ToString() ?? "no verdict"}: {o.Example.Claim} ({o.Reason})");
        return string.Join('\n', lines);
    }
}
