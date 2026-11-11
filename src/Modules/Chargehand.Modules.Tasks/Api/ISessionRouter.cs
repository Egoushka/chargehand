using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Modules.Tasks.Api;

/// <summary>Where a task's next session runs (ADR 0095, D-113).</summary>
/// <param name="Model">The <c>provider/model</c> id the session runs on; null when no pool is set.</param>
/// <param name="Route">The pool and tier that pay for it, recorded on the task's run record; null when no pool is set.</param>
/// <param name="WaitUntil">Set: every pool that may take the session is out until then, and the session does not run.</param>
public sealed record SessionRoute(string? Model, RunRoute? Route, DateTimeOffset? WaitUntil = null);

/// <summary>The budget router as a task runner uses it, once before each session instead of once per batch. A task's data class and size are the router's own; a task only says that a session ran and
/// whether its provider refused it. Without one, every session runs on the batch's model.</summary>
public interface ISessionRouter
{
    /// <summary>How many pools the router drains: a task moves to another pool after a refusal at most this many times.</summary>
    int Pools { get; }

    /// <summary>The pool and model for the next session.</summary>
    /// <exception cref="Chargehand.Kernel.ChargehandException"><c>repository_not_allowed</c>: no pool pays for a model that may see the task's data.</exception>
    Task<SessionRoute> RouteAsync(CancellationToken ct);

    /// <summary>A session on <paramref name="route"/> ended on its provider's refusal: the pool is out until its refill when the <paramref name="budget"/> is spent, else until the next day.</summary>
    void Refused(RunRoute route, bool budget);
}
