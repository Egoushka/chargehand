namespace Chargehand.Modules.Warmer.Api;

/// <summary>What promotion did with a staging folder: the packages now in the shared cache, and the ones left out (as <c>id/version</c>).</summary>
public sealed record PromotionResult(IReadOnlyList<string> Promoted, IReadOnlyList<string> Skipped);

/// <summary>Moves packages a sandboxed restore wrote to its staging folder into the shared cache, outside the sandbox and only after checking
/// them. The staged files themselves are never copied: repository code ran while they were written.</summary>
public interface IPackagePromoter
{
    Task<PromotionResult> PromoteAsync(string stagingDir, CancellationToken ct);
}
