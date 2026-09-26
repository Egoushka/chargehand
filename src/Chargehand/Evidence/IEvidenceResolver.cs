using Chargehand.Contracts;

namespace Chargehand.Evidence;

/// <summary>
/// Checks every evidence reference before a contract leaves its node (ADR 0009): file at commit, diff range,
/// commit, session message, URL seen in the node's inputs or tool output, caller input id. Fetches nothing.
/// </summary>
public interface IEvidenceResolver
{
    Task<IReadOnlyList<EvidenceFailure>> ResolveAsync(ResultContract contract, EvidenceScope scope, CancellationToken ct);
}

public sealed record EvidenceScope(string RepositoryPath, string Commit, string SessionId, IReadOnlyCollection<string> InputIds);

public sealed record EvidenceFailure(string EvidenceId, string Reason);
