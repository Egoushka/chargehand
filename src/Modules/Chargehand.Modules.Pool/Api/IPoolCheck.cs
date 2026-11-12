namespace Chargehand.Modules.Pool.Api;

/// <summary>One condition the governor checks before it starts a worker (ADR 0099). The checks are the capacity.</summary>
public interface IPoolCheck
{
    /// <summary>The reason the scope's queued items carry when this check holds them.</summary>
    string Name { get; }

    /// <summary>Why the scope may not start now, or null when it may.</summary>
    Task<string?> HoldAsync(PoolScope scope, CancellationToken ct);
}

/// <summary>The owner's valve: no new worker while more than <c>max</c> decisions wait on the owner, system-wide.</summary>
/// <param name="open">Counts the open decisions; the host supplies it (the console inbox's <c>decide</c> lane).</param>
public sealed class DecisionsCheck(Func<CancellationToken, Task<int>> open, int max) : IPoolCheck
{
    public string Name => "decisions";

    public async Task<string?> HoldAsync(PoolScope scope, CancellationToken ct)
    {
        var count = await open(ct).ConfigureAwait(false);
        return count > max ? $"{count} decisions wait on the owner; the pool starts at {max} or fewer" : null;
    }
}
