using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Runtime;
using Chargehand.Verification;

namespace Chargehand.Evals;

/// <summary>One line of <c>claims.jsonl</c>: a claim from any arm and the <c>path:line</c> locators it cites.</summary>
public sealed record BenchClaim(string Arm, string Question, int Run, string Text, IReadOnlyList<string> Locators);

/// <summary>Uncited: no locator. Unresolved: a locator names no file, line or range at the commit. Unchecked: the judge gave no valid verdict.</summary>
public enum ClaimOutcome { Uncited, Unresolved, Unsupported, Partial, Supported, Unchecked }

public sealed record ScoredClaim(BenchClaim Claim, ClaimOutcome Outcome, string Reason);

/// <summary>
/// One path to a verdict for every arm of the public benchmark (spec decision 6): the claim's locators are resolved against the
/// pinned checkout, the cited text goes to the shipped <see cref="SupportJudge"/>, and the arm never reaches the judge. Claims are
/// judged together only with those of the same arm, question and run.
/// </summary>
public static class ClaimScorer
{
    private sealed record Line(string Arm, string Question, int Run, string Claim, IReadOnlyList<string> Locators);

    private sealed record Verdict(string Arm, string Question, int Run, string Claim, IReadOnlyList<string> Locators, ClaimOutcome Outcome, string Reason);

    public static async Task<IReadOnlyList<ScoredClaim>> ScoreAsync(IWorkerRuntime runtime, ModelRef? judge, IReadOnlyList<BenchClaim> claims,
        EvidenceScope scope, CancellationToken ct)
    {
        var scored = new ScoredClaim?[claims.Count];
        foreach (var batch in Enumerable.Range(0, claims.Count).GroupBy(i => (claims[i].Arm, claims[i].Question, claims[i].Run)))
            await ScoreBatchAsync(runtime, judge, claims, [.. batch], scope, scored, ct);
        return [.. scored.Select(s => s!)];
    }

    private static async Task ScoreBatchAsync(IWorkerRuntime runtime, ModelRef? judge, IReadOnlyList<BenchClaim> claims, IReadOnlyList<int> indexes,
        EvidenceScope scope, ScoredClaim?[] scored, CancellationToken ct)
    {
        var evidence = new List<Evidence>();
        var resolvable = new List<(int Input, Claim Claim)>();
        foreach (var i in indexes)
        {
            var ids = claims[i].Locators.Select((l, n) => new Evidence($"c{i}e{n}", EvidenceKind.File, l)).ToList();
            if (ids.Count == 0)
            {
                scored[i] = new ScoredClaim(claims[i], ClaimOutcome.Uncited, "cites nothing");
                continue;
            }
            var failures = await new GitEvidenceResolver().ResolveAsync(Contract([], ids), scope, ct);
            if (failures.Count > 0)
            {
                scored[i] = new ScoredClaim(claims[i], ClaimOutcome.Unresolved, string.Join("; ", failures.Select(f => $"{ids.First(e => e.Id == f.EvidenceId).Locator}: {f.Reason}")));
                continue;
            }
            evidence.AddRange(ids);
            resolvable.Add((i, new Claim(claims[i].Text, [.. ids.Select(e => e.Id)], 1)));
        }
        if (resolvable.Count == 0)
            return;

        var cited = await CitedText.ForAsync(Contract([.. resolvable.Select(r => r.Claim)], evidence), scope, ct);
        foreach (var (input, _) in resolvable.Where((_, n) => cited.All(c => c.Index != n)))
            scored[input] = new ScoredClaim(claims[input], ClaimOutcome.Unchecked, "no cited text to judge");
        if (cited.Count == 0)
            return;
        try
        {
            foreach (var v in await SupportJudge.JudgeAsync(runtime, judge, cited, ct))
            {
                var input = resolvable[v.Index].Input;
                scored[input] = new ScoredClaim(claims[input], v.Verdict switch
                {
                    SupportVerdict.Supported => ClaimOutcome.Supported,
                    SupportVerdict.Partial => ClaimOutcome.Partial,
                    _ => ClaimOutcome.Unsupported,
                }, v.Reason);
            }
        }
        catch (InvalidOperationException e)
        {
            foreach (var c in cited)
                scored[resolvable[c.Index].Input] = new ScoredClaim(claims[resolvable[c.Index].Input], ClaimOutcome.Unchecked, e.Message);
        }
    }

    private static ResultContract Contract(IReadOnlyList<Claim> claims, IReadOnlyList<Evidence> evidence) =>
        new("result/v1", "bench", "score", new string('0', 32), new PromptChain([], new AsSent("", "", "", "")), ResultStatus.Completed, "", claims, evidence, [], [], 1,
            new Usage(0, 0, 0, 0, 0));

    public static IReadOnlyList<BenchClaim> ReadClaims(IEnumerable<string> lines) =>
        [.. lines.Where(l => l.Trim().Length > 0).Select(l =>
        {
            var x = JsonSerializer.Deserialize<Line>(l, ContractJson.Options) ?? throw new InvalidDataException("empty claims line");
            return new BenchClaim(x.Arm, x.Question, x.Run, x.Claim, x.Locators ?? []);
        })];

    /// <summary>One verdict line per input claim, in input order; the claim's own fields come first so a line stands alone.</summary>
    public static IEnumerable<string> ToJsonl(IEnumerable<ScoredClaim> scored) =>
        scored.Select(s => JsonSerializer.Serialize(new Verdict(s.Claim.Arm, s.Claim.Question, s.Claim.Run, s.Claim.Text, s.Claim.Locators, s.Outcome, s.Reason), ContractJson.Options));

    public static string ClaimLine(BenchClaim c) => JsonSerializer.Serialize(new Line(c.Arm, c.Question, c.Run, c.Text, c.Locators), ContractJson.Options);

    /// <summary>Outcome counts, one line each, then the unresolved claims with why.</summary>
    public static string Report(IReadOnlyList<ScoredClaim> scored)
    {
        var lines = new List<string> { $"claims {scored.Count}" };
        lines.AddRange(Enum.GetValues<ClaimOutcome>().Select(o => $"  {o.ToString().ToLowerInvariant()}: {scored.Count(s => s.Outcome == o)}"));
        lines.AddRange(scored.Where(s => s.Outcome == ClaimOutcome.Unresolved).Select(s => $"UNRESOLVED [{s.Claim.Question} run {s.Claim.Run}] {s.Claim.Text} ({s.Reason})"));
        return string.Join('\n', lines);
    }
}
