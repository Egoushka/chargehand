using Chargehand.Contracts;

namespace Chargehand.Modules.Research.Api;

/// <summary>
/// Checks every evidence reference before a contract leaves its node (ADR 0009): file at commit, diff range,
/// commit, session message, URL seen in the node's inputs or tool output, caller input id. Fetches nothing.
/// </summary>
public interface IEvidenceResolver
{
    Task<IReadOnlyList<EvidenceFailure>> ResolveAsync(ResultContract contract, EvidenceScope scope, CancellationToken ct);
}

/// <param name="SeenText">Inputs and tool output of the node; a <c>url</c> reference must appear in it.</param>
/// <param name="InputText">The caller's inputs as the node saw them (<c>- id "x" (kind): text</c> per input), for the support check (ADR 0036).</param>
/// <param name="ToolResults">What the node's tools returned; a <c>session_message</c> locator that is not a message id may quote one.</param>
public sealed record EvidenceScope(
    string RepositoryPath,
    string Commit,
    IReadOnlyCollection<string> MessageIds,
    IReadOnlyCollection<string> InputIds,
    string SeenText,
    IReadOnlyList<FileDiff> Diff,
    IReadOnlyList<string>? ToolResults = null,
    string? InputText = null);

public sealed record EvidenceFailure(string EvidenceId, string Reason);

/// <summary>One file of a worker's diff, as its runtime reports it; a <c>diff</c> reference must fall in it.</summary>
public sealed record FileDiff(string File, string Patch, int Additions, int Deletions, string Status);

public static class EvidenceFailures
{
    /// <summary>
    /// Drops evidence that failed to resolve. A claim left without any resolved reference moves to
    /// open_questions with the reasons, so the caller sees it as unverified rather than losing it.
    /// </summary>
    public static ResultContract MoveUnresolved(ResultContract contract, IReadOnlyList<EvidenceFailure> failures)
    {
        if (failures.Count == 0)
            return contract;
        var failed = failures.GroupBy(f => f.EvidenceId).ToDictionary(g => g.Key, g => string.Join("; ", g.Select(f => f.Reason)));
        var claims = new List<Claim>();
        var questions = contract.OpenQuestions.ToList();
        foreach (var claim in contract.Claims)
        {
            var kept = claim.Evidence.Where(id => !failed.ContainsKey(id)).ToList();
            if (kept.Count > 0)
                claims.Add(claim with { Evidence = kept });
            else
                questions.Add($"Unverified: {claim.Text} ({string.Join("; ", claim.Evidence.Select(id => $"{id}: {failed.GetValueOrDefault(id, "unresolved")}"))})");
        }
        var cited = claims.SelectMany(c => c.Evidence).ToHashSet();
        return contract with
        {
            Claims = claims,
            Evidence = contract.Evidence.Where(e => cited.Contains(e.Id)).ToList(),
            OpenQuestions = questions,
        };
    }
}
