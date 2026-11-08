using Chargehand.Modules.Economics.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Driven;

/// <summary>The start gate the tasks of one routed batch report to (ADR 0095). A spent budget on the batch's pool takes that pool out of the router, and halts starts for every batch only when no other pool can take
/// the batch's preset: otherwise the next start falls through to the next pool, and holding it back would only keep money unspent.</summary>
internal sealed class RoutedGate(IStartGate inner, PoolRouting routing, RunRoute route, string preset, string? size, DataClass dataClass) : IStartGate
{
    public DateTimeOffset? HaltedUntil => inner.HaltedUntil;

    public bool TryEnter() => inner.TryEnter();

    public void Exit() => inner.Exit();

    public void RateLimited(DateTimeOffset? retryAfter) => inner.RateLimited(retryAfter);

    public void BudgetExhausted(DateTimeOffset resetsAt)
    {
        routing.Refused(route, budget: true);
        if (!routing.CanRoute(preset, size, dataClass))
            inner.BudgetExhausted(resetsAt);
    }
}
