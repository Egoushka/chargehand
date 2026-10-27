namespace Chargehand.Modules.Tasks.Api;

/// <summary>A process-wide limit on starting tasks that the model gateway sets with its refusals (issue #578). A start takes a slot with <see cref="TryEnter"/> and gives it back with
/// <see cref="Exit"/>; a task's refusal is told with <see cref="RateLimited"/> or <see cref="BudgetExhausted"/>.</summary>
public interface IStartGate
{
    /// <summary>Until when a spent budget holds every start; null when none does.</summary>
    DateTimeOffset? HaltedUntil { get; }

    /// <summary>Takes a slot; false while a budget or a <c>retry-after</c> holds starts, or while the tasks running fill what the pool allows now.</summary>
    bool TryEnter();

    void Exit();

    /// <summary>A plain 429: the pool halves, and no start happens before <paramref name="retryAfter"/> when the gateway named one.</summary>
    void RateLimited(DateTimeOffset? retryAfter);

    /// <summary>A 402, or a 429 naming a budget: nothing starts before <paramref name="resetsAt"/>.</summary>
    void BudgetExhausted(DateTimeOffset resetsAt);
}
