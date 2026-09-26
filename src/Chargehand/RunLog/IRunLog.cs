using System.Diagnostics;
using Chargehand.Contracts;
using Chargehand.Runtime;

namespace Chargehand.RunLog;

/// <summary>
/// Append-only record of runs and model calls; joined to gateway spend afterwards (ADR 0011, ADR 0012). It is also
/// the run store the CLI and the HTTP interface share (ADR 0018): a start record without a run record is a run that
/// has not finished.
/// </summary>
public interface IRunLog
{
    Task AppendAsync(StartRecord record, CancellationToken ct);

    Task AppendAsync(CallRecord record, CancellationToken ct);

    Task AppendAsync(RunRecord record, CancellationToken ct);

    Task<RunEntry> ReadAsync(string runId, CancellationToken ct);
}

/// <summary>Everything the log holds for one run.</summary>
public sealed record RunEntry(StartRecord? Start, RunRecord? Run, IReadOnlyList<CallRecord> Calls);

/// <summary>Written when a run starts: the request as received and the process that runs it.</summary>
/// <param name="ParentRunId">The run this one resends with answers (MCP input_required), if any.</param>
public sealed record StartRecord(string RunId, DateTimeOffset Started, string TraceId, RunRequest Request, int Pid, string? ParentRunId = null)
{
    /// <summary>Whether the process that runs it still exists; without a run record, a dead owner means a lost run.</summary>
    public bool OwnerAlive()
    {
        // ponytail: a reused pid reads as alive; record a process start time too if that ever matters.
        try
        {
            using var p = Process.GetProcessById(Pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
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

/// <summary>A score for a run, 0 to 1: from an eval (source "eval:&lt;name&gt;") or by hand (source "hand").</summary>
public sealed record ScoreRecord(string RunId, string Name, double Value, DateTimeOffset At, string Source);
