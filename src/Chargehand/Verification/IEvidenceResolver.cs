using Chargehand.Contracts;
using Chargehand.Runtime;

namespace Chargehand.Verification;

/// <summary>
/// Checks every evidence reference before a contract leaves its node (ADR 0009): file at commit, diff range,
/// commit, session message, URL seen in the node's inputs or tool output, caller input id. Fetches nothing.
/// </summary>
public interface IEvidenceResolver
{
    Task<IReadOnlyList<EvidenceFailure>> ResolveAsync(ResultContract contract, EvidenceScope scope, CancellationToken ct);
}

/// <param name="SeenText">Inputs and tool output of the node; a <c>url</c> reference must appear in it.</param>
/// <param name="ToolResults">What the node's tools returned; a <c>session_message</c> locator that is not a message id may quote one.</param>
public sealed record EvidenceScope(
    string RepositoryPath,
    string Commit,
    IReadOnlyCollection<string> MessageIds,
    IReadOnlyCollection<string> InputIds,
    string SeenText,
    IReadOnlyList<FileDiff> Diff,
    IReadOnlyList<string>? ToolResults = null);

public sealed record EvidenceFailure(string EvidenceId, string Reason);
