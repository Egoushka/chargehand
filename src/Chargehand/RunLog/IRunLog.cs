using Chargehand.Contracts;

namespace Chargehand.RunLog;

/// <summary>Per-call record; joined to gateway spend after the run (ADR 0011, ADR 0012).</summary>
public interface IRunLog
{
    Task AppendAsync(CallRecord record, CancellationToken ct);
}

public sealed record CallRecord(
    string RunId,
    string NodeId,
    string SessionId,
    string MessageId,
    string Model,
    DateTimeOffset Started,
    TimeSpan Latency,
    Usage Usage,
    PromptChain PromptChain,
    decimal? GatewayUsd = null);
