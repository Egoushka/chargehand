namespace Chargehand.Memory;

/// <summary>Where a memory lives: which configured backend, and a namespace inside it (ADR 0008).</summary>
public sealed record MemoryScope(string Backend, string Namespace);

public sealed record MemoryItem(string Text, string? Context = null, DateTimeOffset? Timestamp = null, string? DocumentId = null, IReadOnlyList<string>? Tags = null);

public sealed record RecalledMemory(string Id, string Text);

/// <summary>
/// Long-term knowledge behind one interface (ADR 0008). Vendor vocabulary stays inside adapters. To invalidate by
/// query, recall first and invalidate the ids.
/// </summary>
public interface IMemoryProvider
{
    Task<IReadOnlyList<RecalledMemory>> RecallAsync(string query, MemoryScope scope, CancellationToken ct);

    Task RetainAsync(MemoryItem item, MemoryScope scope, CancellationToken ct);

    /// <summary>Soft-retires one memory; adapters keep it reversible where the backend allows.</summary>
    Task InvalidateAsync(string id, string reason, MemoryScope scope, CancellationToken ct);
}
