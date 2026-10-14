namespace Chargehand.Modules.Journal.Api;

/// <summary>One event of a batch as the journal keeps it (ADR 0061).</summary>
/// <param name="Sequence">Increases with every event the journal appends, across batches: a batch's events read back in this order, and a client that saw one resumes after it.</param>
/// <param name="Kind">The event's kind, snake case.</param>
/// <param name="Payload">The event as JSON, written by the caller.</param>
public sealed record BatchEvent(long Sequence, string Kind, DateTimeOffset At, string Payload);
