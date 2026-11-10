using Chargehand.Contracts;
using Chargehand.Modules.Economics.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Driven;

/// <summary>The budget router as one batch's tasks use it, before each session (ADR 0095, D-113): the batch's preset, size and data class are fixed at its start, and the data class is the one the batch was
/// classified with, never worked out again. A session the router cannot place for now is an answer (<see cref="SessionRoute.WaitUntil"/>), not an exception; a batch no pool may serve still throws
/// <c>repository_not_allowed</c>.</summary>
/// <param name="unrouted">What a session runs on with the router off; the router answers for itself when it is on, and this is only what <see cref="PoolRouting.RouteAsync"/> is given.</param>
internal sealed class PoolSessionRouter(PoolRouting routing, string preset, string? size, string? unrouted, DataClass dataClass) : ISessionRouter
{
    public int Pools => routing.Pools;

    public async Task<SessionRoute> RouteAsync(CancellationToken ct)
    {
        try
        {
            var routed = await routing.RouteAsync(preset, size, unrouted, dataClass, ct);
            return new SessionRoute(routed.Model, routed.Route);
        }
        catch (PoolsWaitException wait)
        {
            return new SessionRoute(null, null, wait.Wait.Until);
        }
    }

    public void Refused(RunRoute route, bool budget) => routing.Refused(route, budget);
}
