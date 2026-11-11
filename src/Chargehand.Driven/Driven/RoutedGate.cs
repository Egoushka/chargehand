using Chargehand.Modules.Economics.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Driven;

/// <summary>The start gate the tasks of one routed batch report to (ADR 0095, D-113). The tasks' session router has told the router which pool refused a session; a spent budget halts starts for every batch
/// only when no other pool can take the batch's preset: otherwise the next start falls through to the next pool, and holding it back would only keep money unspent.</summary>
internal sealed class RoutedGate(IStartGate inner, PoolRouting routing, string preset, string? size, DataClass dataClass) : IStartGate
{
    public DateTimeOffset? HaltedUntil => inner.HaltedUntil;

    public bool TryEnter() => inner.TryEnter();

    public void Exit() => inner.Exit();

    public void RateLimited(DateTimeOffset? retryAfter) => inner.RateLimited(retryAfter);

    public void BudgetExhausted(DateTimeOffset resetsAt)
    {
        if (!routing.CanRoute(preset, size, dataClass))
            inner.BudgetExhausted(resetsAt);
    }
}
