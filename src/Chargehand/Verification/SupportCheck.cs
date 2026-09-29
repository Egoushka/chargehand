using Chargehand.Contracts;
using Chargehand.Runtime;

namespace Chargehand.Verification;

/// <summary>
/// Applies the support judge's verdicts to a result (ADR 0036). Supported claims stay and say so; a partly supported claim stays at
/// half its confidence with the reason in <c>open_questions</c>; an unsupported claim moves to <c>open_questions</c>, as an
/// unresolved one does. It never fails a run: when the check cannot run, every claim is <c>unchecked</c> and one open question says why.
/// </summary>
public static class SupportCheck
{
    private const int MaxReasonLength = 200;

    public static ResultContract Apply(ResultContract contract, IReadOnlyList<ClaimVerdict> verdicts)
    {
        var byIndex = verdicts.ToDictionary(v => v.Index);
        var claims = new List<Claim>();
        var questions = contract.OpenQuestions.ToList();
        for (var i = 0; i < contract.Claims.Count; i++)
        {
            var claim = contract.Claims[i];
            if (!byIndex.TryGetValue(i, out var v))
            {
                claims.Add(claim with { Support = ClaimSupport.Unchecked });
                continue;
            }
            var reason = v.Reason.Length == 0 ? "" : $" ({v.Reason})";
            switch (v.Verdict)
            {
                case SupportVerdict.Supported:
                    claims.Add(claim with { Support = ClaimSupport.Supported });
                    break;
                case SupportVerdict.Partial:
                    claims.Add(claim with { Support = ClaimSupport.Partial, Confidence = claim.Confidence / 2 });
                    questions.Add($"Partly supported by its citation: {claim.Text}{reason}");
                    break;
                default:
                    questions.Add($"Unsupported by its citation: {claim.Text}{reason}");
                    break;
            }
        }
        var cited = claims.SelectMany(c => c.Evidence).ToHashSet();
        return contract with { Claims = claims, Evidence = contract.Evidence.Where(e => cited.Contains(e.Id)).ToList(), OpenQuestions = questions };
    }

    /// <summary>Records the hash of the cited text on each evidence entry that has none, so a reader can recompute it from the pinned commit.</summary>
    internal static ResultContract WithHashes(ResultContract contract, IReadOnlyList<CitedClaim> cited)
    {
        var hashes = cited.SelectMany(c => c.Hashes ?? new Dictionary<string, string>()).GroupBy(h => h.Key).ToDictionary(g => g.Key, g => g.First().Value);
        return contract with { Evidence = [.. contract.Evidence.Select(e => e.Sha256 is null && hashes.TryGetValue(e.Id, out var h) ? e with { Sha256 = h } : e)] };
    }

    /// <summary>The check could not run: nothing is dropped, nothing is claimed about support.</summary>
    public static ResultContract Unavailable(ResultContract contract, string reason) => contract with
    {
        Claims = [.. contract.Claims.Select(c => c with { Support = ClaimSupport.Unchecked })],
        OpenQuestions = [.. contract.OpenQuestions, $"Support check unavailable: {reason}"],
    };

    /// <summary>Checks a completed result's claims; any other result is returned as it is. Never throws except for cancellation.</summary>
    public static async Task<ResultContract> RunAsync(IWorkerRuntime runtime, ModelRef? model, ResultContract contract, EvidenceScope scope, CancellationToken ct)
    {
        if (contract.Status != ResultStatus.Completed || contract.Claims.Count == 0)
            return contract;
        try
        {
            var cited = await CitedText.ForAsync(contract, scope, ct);
            if (cited.Count == 0)
                return Apply(contract, []);
            return WithHashes(Apply(contract, await SupportJudge.JudgeAsync(runtime, model, cited, ct)), cited);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var reason = ChargehandException.Scrub(e.Message).ReplaceLineEndings(" ");
            return Unavailable(contract, reason.Length <= MaxReasonLength ? reason : reason[..MaxReasonLength]);
        }
    }
}
