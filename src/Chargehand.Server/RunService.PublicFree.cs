using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Microsoft.AspNetCore.Http;

namespace Chargehand.Server;

public sealed partial class RunService
{
    /// <summary>The free-model pool (ADR 0093); null: the profile has no <c>public_free</c> block.</summary>
    public PublicFreeGate? PublicFree => orchestrator.PublicFree;

    /// <summary>Moves a run on a qualifying public repository to the pool preset, or refuses a request that names the pool preset where it may not run. <paramref name="Pooled"/> says the run
    /// now uses the pool, whose credential is not the server's.</summary>
    private async Task<(RunRequest Request, AccountRefusal? Refusal, bool Pooled)> PoolStepAsync(RunRequest request, CancellationToken ct)
    {
        if (PublicFree is null)
            return (request, null, false);
        var decision = await PublicFree.DecideAsync(request, ct);
        return decision.Choice switch
        {
            PoolChoice.Refused => (request, new(decision.Code!, decision.Reason!, StatusCodes.Status403Forbidden), false),
            PoolChoice.Pool => (request with { Context = request.Context with { Preset = decision.Preset! } }, null, true),
            _ => (request, null, false),
        };
    }

    /// <summary>Whether the run spends the pool's free key and nothing priced: the approval gate for paid spend has nothing to hold (ADR 0092).</summary>
    private bool UsesPool(RunRequest request) =>
        PublicFree is not null && request.Driven is null && string.Equals(request.Context.Preset, PublicFree.Preset, StringComparison.Ordinal);
}
