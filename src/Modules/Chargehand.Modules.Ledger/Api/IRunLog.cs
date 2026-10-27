using System.Diagnostics;
using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Modules.Ledger.Api;

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

    /// <summary>One <see cref="RunSummary"/> per run, newest first, for a client that lists runs (ADR 0039).</summary>
    Task<IReadOnlyList<RunSummary>> ListAsync(RunListQuery query, CancellationToken ct);

    /// <summary>What <paramref name="actor"/>'s runs cost since <paramref name="since"/> (ADR 0050).</summary>
    Task<ActorSpend> SpendAsync(string actor, DateTimeOffset since, CancellationToken ct) => throw new NotSupportedException();

    /// <summary>Every record, for the operator console's read routes (batches, inbox, analytics); a log that cannot list them says so with <see cref="NotSupportedException"/>.</summary>
    Task<RunLogData> ReadAllAsync(CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>An account's spend in a window: finished runs by their result's usage, unfinished ones by their calls so far. A task or call of a driven batch is not counted
/// apart: the batch's result holds its cost. <c>Usd</c> counts only what an API key was billed (ADR 0082): a subscription call adds tokens and no dollars, so a token cap is the
/// bound that always binds there. <c>UsdAtApiPrice</c> is every call at the profile's table, whoever paid.</summary>
public sealed record ActorSpend(decimal Usd, long Tokens, decimal UsdAtApiPrice = 0);

/// <summary>Which credential paid for a call or run, from the runtime's auth source and never from the model's name.</summary>
public static class CredentialKind
{
    public const string ApiKey = "api_key";
    public const string Subscription = "subscription";

    /// <summary>Whether the record's dollars were billed: a record from before the field was an API-priced one.</summary>
    public static bool Billed(string? credential) => credential != Subscription;
}

/// <param name="Status">Only runs in this state; null for all.</param>
/// <param name="Since">Only runs started at or after this time.</param>
/// <param name="Actor">Only this account's runs (ADR 0050); null for all.</param>
/// <param name="Kind">Only <c>single</c> or only <c>batch</c> runs; null for both.</param>
/// <param name="Repository">Only runs whose <c>context.repository.path</c> is exactly this; null for all.</param>
/// <param name="RunId">Only this run; null for all.</param>
public sealed record RunListQuery(RunState? Status = null, DateTimeOffset? Since = null, int Limit = 50, string? Actor = null, string? Kind = null, string? Repository = null, string? RunId = null);

/// <summary>Everything the log holds for one run.</summary>
public sealed record RunEntry(StartRecord? Start, RunRecord? Run, IReadOnlyList<CallRecord> Calls);

/// <summary>Written when a run starts: the request as received and the process that runs it.</summary>
/// <param name="ParentRunId">The run this one resends with answers (MCP input_required), if any.</param>
/// <param name="BootId">The <see cref="ProcessBoot.Id"/> of the writing process; null on a record from before it existed. A pid alone repeats (a container's server is pid 1 every time).</param>
/// <param name="Actor">The account that started the run (ADR 0050); null for a CLI run and for a record from before accounts.</param>
public sealed record StartRecord(string RunId, DateTimeOffset Started, string TraceId, RunRequest Request, int Pid, string? ParentRunId = null, string? BootId = null, string? Actor = null)
{
    /// <summary>Whether the process that runs it still exists; without a run record, a dead owner means a lost run.</summary>
    public bool OwnerAlive()
    {
        if (BootId == ProcessBoot.Id)
            return true;
        // No boot id: written before it existed, by a process that cannot be told from a later one with the same pid.
        if (BootId is null || Pid == Environment.ProcessId)
            return false;
        // ponytail: another process's reused pid still reads as alive (a CLI run); the boot id fixes this process's own restarts.
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

/// <summary>A value that differs for every process start, so a record can tell its writer from a later process with the same pid.</summary>
public static class ProcessBoot
{
    public static string Id { get; } = Guid.NewGuid().ToString("N");
}

/// <param name="Tokens">Null for calls whose usage OpenCode does not report (intake via generate).</param>
/// <param name="Usd">Priced with the profile's table; null when tokens are unknown.</param>
/// <param name="ForkedFrom">The session this call's session was forked from, if any (cache report).</param>
/// <param name="Credential">The <see cref="CredentialKind"/> that paid; null on a record from before it existed (counts as an API key).</param>
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
    IReadOnlyList<InstructionRef>? Instructions = null,
    string? Credential = null)
{
    public long? PromptTokens => Tokens is null ? null : Tokens.Input + Tokens.CacheRead + Tokens.CacheWrite;

    public double? CacheRate => Tokens is null || PromptTokens == 0 ? null : (double)Tokens.CacheRead / PromptTokens!.Value;
}

public sealed record InstructionRef(string Key, string Sha256);

/// <param name="Credential">The <see cref="CredentialKind"/> that paid for the run; null on a record from before it existed (counts as an API key).</param>
/// <param name="ExecutedAction">What ran: intake's action, or "answer" when the preset or the split plan did not allow it.</param>
public sealed record RunRecord(
    string RunId,
    DateTimeOffset Started,
    DateTimeOffset Finished,
    string Preset,
    string? IntakeAction,
    TaskSpec? Spec,
    ResultContract Result,
    string? ExecutedAction = null,
    ExtensionsReport? Extensions = null,
    string? Actor = null,
    string? Credential = null);

/// <summary>What the profile's extensions did in one run, for the run log and <c>chargehand show</c> (goal 0.6). Null on a record from before it existed.</summary>
public sealed record ExtensionsReport(IReadOnlyList<MemoryReport> Memory, IReadOnlyList<ServiceReport> Services)
{
    /// <summary>One line per memory source: <c>memory notes: recalled 2, retained 1</c>; a skipped step says so and why in place of its count.
    /// Then one per service: <c>service docs: granted read_doc (tool_missing: x)</c>, or <c>dropped</c> when no tool was granted.</summary>
    public IReadOnlyList<string> Lines() =>
        [.. Memory.Select(m => $"memory {m.Source}: {(m.RecallSkipped is null ? $"recalled {m.Recalled}" : $"recall skipped ({m.RecallSkipped})")}, "
            + (m.RetainSkipped is null ? $"retained {m.Retained}" : $"retain skipped ({m.RetainSkipped})")),
         .. Services.Select(s => $"service {s.Server}: {(s.Tools.Count > 0 ? $"granted {string.Join(", ", s.Tools)}" : "dropped")}"
            + (s.Issues.Count > 0 ? $" ({string.Join("; ", s.Issues)})" : ""))];
}

/// <param name="RecallSkipped">Why the source recalled nothing (failed, timed out); null when it answered.</param>
/// <param name="RetainSkipped">Why the source retained nothing when it should have: the retain failed, the run had no commit, or no claim qualified; null otherwise.</param>
public sealed record MemoryReport(string Source, int Recalled, string? RecallSkipped, int Retained, string? RetainSkipped);

/// <summary>A service a preset asked for: its tools as granted, and why part of the request was dropped.</summary>
public sealed record ServiceReport(string Server, IReadOnlyList<string> Tools, IReadOnlyList<string> Issues);

/// <summary>A score for a run, 0 to 1: from an eval (source "eval:&lt;name&gt;") or by hand (source "hand").</summary>
public sealed record ScoreRecord(string RunId, string Name, double Value, DateTimeOffset At, string Source);
