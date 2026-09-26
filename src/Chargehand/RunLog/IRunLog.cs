using Chargehand.Contracts;
using Chargehand.Runtime;

namespace Chargehand.RunLog;

/// <summary>Append-only record of runs and model calls; joined to gateway spend afterwards (ADR 0011, ADR 0012).</summary>
public interface IRunLog
{
    Task AppendAsync(CallRecord record, CancellationToken ct);

    Task AppendAsync(RunRecord record, CancellationToken ct);

    Task<(RunRecord? Run, IReadOnlyList<CallRecord> Calls)> ReadAsync(string runId, CancellationToken ct);
}

/// <param name="Tokens">Null for calls whose usage OpenCode does not report (intake via generate).</param>
/// <param name="Usd">Priced with the profile's table; null when tokens are unknown.</param>
/// <param name="ForkedFrom">The session this call's session was forked from, if any (cache report).</param>
/// <param name="Instructions">The node's instruction entries by key, hashed, in the order they were set (cache report).</param>
public sealed record CallRecord(
    string RunId,
    string NodeId,
    string Kind,
    string? SessionId,
    string? MessageId,
    string Model,
    DateTimeOffset Started,
    double LatencyMs,
    TokenCounts? Tokens,
    decimal? Usd,
    PromptChain PromptChain,
    string? ForkedFrom = null,
    IReadOnlyList<InstructionRef>? Instructions = null)
{
    public long? PromptTokens => Tokens is null ? null : Tokens.Input + Tokens.CacheRead + Tokens.CacheWrite;

    public double? CacheRate => Tokens is null || PromptTokens == 0 ? null : (double)Tokens.CacheRead / PromptTokens!.Value;
}

public sealed record InstructionRef(string Key, string Sha256);

/// <param name="ExecutedAction">What ran: intake's action, or "answer" when the preset or the split plan did not allow it.</param>
public sealed record RunRecord(
    string RunId,
    DateTimeOffset Started,
    DateTimeOffset Finished,
    string Preset,
    string? IntakeAction,
    TaskSpec? Spec,
    ResultContract Result,
    string? ExecutedAction = null);
