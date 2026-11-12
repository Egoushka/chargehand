namespace Chargehand.Modules.Pool.Api;

/// <summary>What the governor hands the starter: the scope, the new worker's id, its claimed items and the card its predecessor left.</summary>
public sealed record PoolStartRequest(PoolScope Scope, string Worker, IReadOnlyList<PoolItem> Items, ResumeCard? Card);

/// <summary>Turns a worker into running work; the host's admission (money, headroom, the box) lives behind it.</summary>
public interface IPoolStarter
{
    /// <summary>Null when the worker started; otherwise the refusal, which the governor records as reason <c>starter</c>.</summary>
    Task<string?> StartAsync(PoolStartRequest request, CancellationToken ct);
}
