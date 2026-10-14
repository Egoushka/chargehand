using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Journal.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Modules.Batches.Api;

/// <summary>A usage or rate limit that holds a batch's new starts: the task that hit it, when it was seen and when it lifts.</summary>
public sealed record RateLimitPause(string BatchRunId, string TaskId, DateTimeOffset ObservedAt, DateTimeOffset ResetsAt);

/// <summary>How a batch is started: the request and who sent it, and what the host's wiring supplies of its profile.</summary>
/// <param name="Resume">A batch a restart interrupted, as <see cref="BatchLifecycle.UnfinishedAsync"/> read it back: it carries on under the same run id (ADR 0043).</param>
/// <param name="Actor">The account that started the batch (ADR 0050); a resumed batch keeps the one it started with.</param>
/// <param name="ClaudeVersion">The Claude Code version the profile names, for the result's prompt chain.</param>
/// <param name="LoadKey">The key the result is signed with; null: results are not signed. A key that cannot be loaded fails the batch.</param>
public sealed record BatchRunStart(RunRequest Request, string RunId, Action<RunStatus>? Progress, Func<bool>? CancelledByCaller, OpenBatch? Resume, string? Actor, string ClaudeVersion,
    Func<ECDsa?> LoadKey);

/// <summary>One batch as the host's wiring runs it, between its start record and its result.</summary>
/// <param name="Sign">Signs a task's result with the batch's key; null: results are not signed.</param>
/// <param name="Progress">The batch's events, to the caller and the records.</param>
/// <param name="Span">The batch's span; null when nothing listens.</param>
/// <param name="Detaching">The server is stopping, as against a caller who cancels: the batch is left for the next process.</param>
public sealed record BatchRunContext(RunRequest Request, string RunId, DateTimeOffset Started, string TraceId, Action<RunStatus>? Progress, Activity? Span, OpenBatch? Resume,
    Func<bool> Detaching, Func<bool> CancelledByCaller, string? Actor, CancellationToken Ct, Func<ResultContract, ResultContract>? Sign = null);

/// <summary>The host's part of a batch: from its resolved tasks to its result, with everything the profile decides.</summary>
public delegate Task<ResultContract> BatchBody(BatchRunContext context);

/// <summary>A driven batch from its start to its run record (ADR 0039, ADR 0043, ADR 0061): its events kept and journaled, its start record, its body, a failure turned into a failed result,
/// the result signed and recorded, its steps forgotten and <c>run_finished</c> sent. A server that is stopping takes the detached exit: no run record, and the batch's steps stay for the next
/// process. Also what an operator and the console read of the batches this process carries. The profile's wiring is the host's, given as a <see cref="BatchBody"/>.</summary>
/// <param name="stateDirectory">Where the journal and the token key files are kept; null: nothing outlives the process.</param>
/// <param name="records">Where a batch's events are kept for the console (CHARGEHAND-214); null: they are not.</param>
public sealed partial class BatchLifecycle(IRunLog log, string? stateDirectory = null, BatchRecords? records = null) : IDisposable
{
    private readonly OpenBatches? _batches = stateDirectory is null ? null : new OpenBatches(stateDirectory, log);

    private readonly ConcurrentDictionary<string, RateLimitPause> _pauses = new();

    private readonly ConcurrentDictionary<string, (BatchControl Control, Action<RunStatus>? Publish)> _live = new(StringComparer.Ordinal);

    /// <summary>The journal of the batches' side effects (ADR 0061); null when this server keeps no state.</summary>
    public Task<DrivenJournal>? JournalAsync() => _batches?.JournalAsync();

    /// <summary>What an operator can do to a batch this process is running now (CHARGEHAND-214); null for any other.</summary>
    public BatchControl? ControlOf(string batchRunId) => _live.TryGetValue(batchRunId, out var live) ? live.Control : null;

    /// <summary>Tells a batch's events something an operator did: into the live stream and the records while this process runs it, into the records alone for a batch that has finished.</summary>
    public void Announce(string batchRunId, RunStatus e)
    {
        if (_live.TryGetValue(batchRunId, out var live) && live.Publish is { } publish)
            publish(e);
        else
            records?.AppendEvent(batchRunId, e);
    }

    /// <summary>Whether a batch's events are journaled, so <see cref="JournaledEventsAsync"/> can replay them.</summary>
    public bool JournalsEvents => _batches is not null && records is not null;

    /// <summary>A batch's journaled events after sequence <paramref name="after"/>, oldest first: those of this process and of one a restart interrupted. Empty when <see cref="JournalsEvents"/> is off.</summary>
    public async Task<IReadOnlyList<(long Sequence, RunStatus Event)>> JournaledEventsAsync(string batch, long after, CancellationToken ct) =>
        JournalsEvents ? BatchRecords.Parse(await (await _batches!.JournalAsync()).ReadEventsAsync(batch, after, ct: ct)) : [];

    /// <summary>The unfinished batches this process carries (ADR 0043): those it started, and those its predecessor left, read back by <see cref="UnfinishedAsync"/> at the start; each with its
    /// tasks as the journal folds them now.</summary>
    public IReadOnlyList<OpenBatch> LiveBatches() => _batches?.Live ?? [];

    /// <summary>Usage or rate limits that hold new starts now, one per batch.</summary>
    public IReadOnlyList<RateLimitPause> RateLimitPauses(DateTimeOffset now) => [.. _pauses.Values.Where(p => p.ResetsAt > now).OrderBy(p => p.ResetsAt)];

    /// <summary>The batches a restart interrupted, read back from the journal and the run log (<see cref="OpenBatches.LoadAsync"/>), each with its tasks as the journal folds them. A snapshot
    /// written before the journal held a batch's fields is imported into it first.</summary>
    /// <param name="resumable">This server can run a batch (driven sessions are on); when not, none is returned, though <see cref="LiveBatches"/> still shows them.</param>
    public async Task<IReadOnlyList<OpenBatch>> UnfinishedAsync(bool resumable, CancellationToken ct)
    {
        var open = _batches is null ? [] : await _batches.LoadAsync(ct);
        PruneRecords();
        return resumable ? open : [];
    }

    public async Task<ResultContract> RunAsync(BatchRunStart start, BatchBody body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(body);
        var progress = await RecordedAsync(start.RunId, start.Progress);
        bool CancelledByCaller() => start.CancelledByCaller?.Invoke() == true;
        // A server that is stopping, as against a caller who cancels, leaves the batch for the next process to carry on; only when its state is kept.
        bool Detaching() => _batches is not null && ct.IsCancellationRequested && !CancelledByCaller();
        // As for an orchestrator run: under serve the request's activity is not recorded, and a parent-based sampler would drop the batch's spans.
        if (Activity.Current is { Recorded: false })
            Activity.Current = null;
        using var span = Telemetry.Source.StartActivity(DrivenTelemetry.RunSpan);
        var context = new BatchRunContext(start.Request, start.RunId, start.Resume?.Started ?? DateTimeOffset.UtcNow, TraceOf(start.Resume, span), progress, span, start.Resume, Detaching,
            CancelledByCaller, start.Actor ?? start.Resume?.Actor, ct);
        Tag(span, start);
        // A resumed batch is written again so the log names the process that runs it now.
        await log.AppendAsync(new StartRecord(context.RunId, context.Started, context.TraceId, context.Request, Environment.ProcessId, BootId: ProcessBoot.Id, Actor: context.Actor), ct);
        progress?.Invoke(RunStatus.Of(context.RunId, RunState.Running, RunEventKind.Started));
        var (result, key) = await RunBodyAsync(context, start, body);
        using (key)
            return Detaching() ? result : await FinishAsync(context, Signed(result with { Actor = context.Actor }, key));
    }

    /// <summary>The batch's events go to disk as well as to the caller, so its timeline outlives a restart (CHARGEHAND-214), and to the journal, so a client that reconnects replays them.</summary>
    private async Task<Action<RunStatus>?> RecordedAsync(string runId, Action<RunStatus>? caller)
    {
        if (records is null)
            return caller;
        if (_batches is not null)
            records.Journal(runId, await _batches.JournalAsync());
        return e =>
        {
            records.AppendEvent(runId, e);
            caller?.Invoke(e);
        };
    }

    private static string TraceOf(OpenBatch? resume, Activity? span) => resume?.TraceId ?? span?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString();

    private static void Tag(Activity? span, BatchRunStart start)
    {
        if (span is null)
            return;
        span.SetTag("langfuse.trace.name", DrivenTelemetry.RunSpan);
        span.SetTag("chargehand.run_id", start.RunId);
        span.SetTag("chargehand.preset", start.Request.Context.Preset);
        span.SetTag("chargehand.repository", start.Request.Context.Repository?.Path);
        span.SetTag("chargehand.driven.task_count", start.Request.Driven?.Tasks.Count);
    }

    /// <summary>The body under the batch's control, with the key loaded first; an exception other than a server's stop becomes a failed result.</summary>
    private async Task<(ResultContract Result, ECDsa? Key)> RunBodyAsync(BatchRunContext context, BatchRunStart start, BatchBody body)
    {
        ECDsa? key = null;
        _live[context.RunId] = (new BatchControl(), context.Progress);
        try
        {
            key = start.LoadKey();
            return (await body(key is null ? context : context with { Sign = r => ResultSignature.Sign(r, key) }), key);
        }
        catch (Exception e) when (e is not OperationCanceledException || context.CancelledByCaller())
        {
            return (Failed(context, start.ClaudeVersion, e), key);
        }
        finally
        {
            _live.TryRemove(context.RunId, out _);
        }
    }

    private static ResultContract Failed(BatchRunContext context, string claudeVersion, Exception e)
    {
        var error = e is OperationCanceledException
            ? new ResultError(ErrorCode.Cancelled, "the run was cancelled", false, "Start the batch again if it is still wanted; nothing was pushed.")
            : ChargehandException.ErrorOf(e);
        var chain = new PromptChain([], new AsSent($"claude-code/{claudeVersion}", "driven", "batch", context.Started.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        return new ResultContract("result/v1", context.RunId, "driven", context.TraceId, chain, ResultStatus.Failed, error.Message, [], [], [], [error.Message], 0, new Usage(0, 0, 0, 0, 0), error);
    }

    private static ResultContract Signed(ResultContract result, ECDsa? key) => key is null ? result : ResultSignature.Sign(result, key);

    /// <summary>The run record, then the batch's steps forgotten, then <c>run_finished</c>.</summary>
    private async Task<ResultContract> FinishAsync(BatchRunContext context, ResultContract result)
    {
        context.Span?.SetTag("chargehand.contract.status", JsonNamingPolicy.SnakeCaseLower.ConvertName(result.Status.ToString()));
        context.Span?.SetTag("chargehand.error.code", result.Error is { } failure ? JsonNamingPolicy.SnakeCaseLower.ConvertName(failure.Code.ToString()) : null);
        if (result.Status != ResultStatus.Completed)
            context.Span?.SetStatus(ActivityStatusCode.Error, result.Error?.Message);
        await log.AppendAsync(new RunRecord(context.RunId, context.Started, DateTimeOffset.UtcNow, context.Request.Context.Preset, null, null, result, Actor: context.Actor),
            context.CancelledByCaller() ? CancellationToken.None : context.Ct);
        await EndStepsAsync(context.RunId);
        _pauses.TryRemove(context.RunId, out _);
        context.Progress?.Invoke(RunStatus.Of(context.RunId, RunStatus.StateOf(result.Status), RunEventKind.RunFinished) with { Result = result });
        PruneRecords();
        return result;
    }

    /// <summary>Forgets a finished batch's flows and its token key file (<see cref="OpenBatches.EndAsync"/>). A journal that cannot be written does not fail a batch that has its run record: the
    /// next start ends it again.</summary>
    private async Task EndStepsAsync(string runId)
    {
        if (_batches is null)
            return;
        try
        {
            await _batches.EndAsync(runId);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine($"chargehand: the step journal could not forget batch {runId}: {e.Message}");
            _batches.Drop(runId);
        }
    }

    /// <summary>Deletes the records of the oldest batches (<see cref="BatchRecords.Prune"/>), never those of an unfinished batch.</summary>
    private void PruneRecords()
    {
        if (records is null)
            return;
        try
        {
            records.Prune(LiveBatches().Select(b => b.RunId).ToHashSet(StringComparer.Ordinal));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"chargehand: could not prune the batch records: {e.Message}");
        }
    }

    public void Dispose() => _batches?.Dispose();
}
