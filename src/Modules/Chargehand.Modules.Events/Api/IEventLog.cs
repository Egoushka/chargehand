namespace Chargehand.Modules.Events.Api;

/// <summary>One envelope to store, with the columns the filters read. <paramref name="Envelope"/> is the normalized JSON object, without <c>seq</c>.</summary>
public sealed record NewEvent(string Source, string Id, string Type, string? Subject, string Scope, string? CorrelationId, string Time, string Envelope);

/// <summary>One stored envelope. <see cref="Json"/> is the envelope with its <c>seq</c>.</summary>
public sealed record StoredEvent(long Seq, string Source, string Id, string Type, string? Subject, string Scope, string? CorrelationId, string Time, string Envelope)
{
    public string Json => Envelope.Length > 2 ? $"{{\"seq\":{Seq},{Envelope[1..]}" : $"{{\"seq\":{Seq}}}";
}

/// <summary>A read of the log, oldest first unless <paramref name="Newest"/>. The globs follow <see cref="Glob"/>; <paramref name="Readable"/> is the caller's read grants,
/// null for all.</summary>
/// <param name="After">Only events with a <c>seq</c> after it.</param>
/// <param name="UpTo">Only events with a <c>seq</c> up to it.</param>
/// <param name="Since">Only events whose <c>time</c> is at or after it (UTC, in the <see cref="Envelope.Format"/> text).</param>
/// <param name="Until">Only events whose <c>time</c> is at or before it.</param>
/// <param name="Correlation">Only events with this <c>correlationid</c>.</param>
/// <param name="Actor">Only events whose <c>actor</c> has a <c>name</c>, <c>id</c> or <c>run</c> matching this glob.</param>
/// <param name="Thread">Only events whose <c>subject</c> or <c>correlationid</c> is it.</param>
public sealed record EventFilter(long After = 0, long? UpTo = null, string? Type = null, string? Source = null, string? Scope = null, string? Subject = null,
    string? Thread = null, IReadOnlyList<string>? Readable = null, int Limit = 100, bool Newest = false, string? Since = null, string? Until = null, string? Correlation = null, string? Actor = null);

/// <summary>The store the events spec's Task 2 describes: an idempotent append keyed by <c>(source, id)</c>, a batch as one transaction, reads across sources, and a
/// signal after each commit. Shaped like switchknife's <c>IEventFeed</c>, so the store can move there (flow = source, key = id, kind = type, tag = scope).</summary>
public interface IEventLog
{
    /// <summary>Stores the batch in one transaction and returns each event's <c>seq</c>; a repeat of <c>(source, id)</c> returns the first <c>seq</c> and stores nothing.</summary>
    Task<IReadOnlyList<long>> AppendAsync(IReadOnlyList<NewEvent> batch, CancellationToken ct);

    Task<IReadOnlyList<StoredEvent>> ReadAsync(EventFilter filter, CancellationToken ct);

    EventSignal Signal { get; }
}
