using Chargehand.Config;
using Chargehand.RunLog;

namespace Chargehand.Runtime;

/// <summary>
/// Turns a preset's <c>services</c> into grants for one run (ADR 0034). What does not resolve (a server the profile lacks, a
/// secret no source answers, a server that is down, a tool it does not list) is dropped and reported, never thrown: only the
/// caller's own cancellation ends a call early.
/// </summary>
public interface IServiceResolver
{
    Task<ResolvedServices> ResolveAsync(IReadOnlyList<ServiceUse> uses, CancellationToken ct);
}

/// <param name="Grants">One per server with at least one tool left, in the preset's order.</param>
/// <param name="Report">One per server the preset named: its granted tools and what was dropped (issues read <c>code: detail</c>,
/// the code one of <c>unknown_server</c>, <c>secret_unresolved</c>, <c>unreachable</c>, <c>tool_missing</c>).</param>
public sealed record ResolvedServices(IReadOnlyList<ServiceGrant> Grants, IReadOnlyList<ServiceReport> Report);
