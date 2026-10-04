using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Driven;

namespace Chargehand.Server;

/// <summary>
/// Runs requests in the background, one at a time: ADR 0011 allows 2 concurrent nodes per model, and a split already
/// uses both. A run outlives the HTTP or MCP call that started it. Start and run records live in the run log, which
/// the CLI shares (ADR 0018); events live here only while the run is unfinished.
/// </summary>
public sealed class RunService(Orchestrator orchestrator, string presetsDirectory, CancellationToken stopping, DrivenRun? driven = null) : IDisposable
{
    /// <summary>Unfinished runs held at most; beyond it a new run is refused (HTTP 429).</summary>
    public const int MaxUnfinished = 10;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Runs a driven session starts through its token (research, review) use this gate, not the main one: the batch that holds the main gate
    /// is waiting for them, so sharing it would deadlock (ADR 0039).</summary>
    private readonly SemaphoreSlim _childGate = new(2, 2);
    private readonly ConcurrentDictionary<string, RunHandle> _runs = new();

    public void Dispose()
    {
        _gate.Dispose();
        _childGate.Dispose();
        driven?.Dispose();
    }

    public RunTokenService Tokens { get; } = new();

    public RunTokenLedger Ledger { get; } = new();

    /// <summary>The kill switch is on: no new run starts until <see cref="Resume"/> (ADR 0039).</summary>
    public bool Halted { get; private set; }

    private string MintToken(TaskGrant g) =>
        Tokens.Issue(new RunTokenClaims(g.RunId, g.RepositoryPath, g.Commit, g.MaxUsd, g.MaxTokens, g.Expires), DateTimeOffset.UtcNow);

    public const string HaltedMessage = "the server is halted; POST /v1/resume to accept runs again";

    /// <summary>Cancels a run this process holds, on purpose. False when it holds no such run.</summary>
    public bool Cancel(string runId)
    {
        if (_runs.GetValueOrDefault(runId) is not { } run)
            return false;
        run.CancelByCaller();
        return true;
    }

    /// <summary>Stops accepting runs and cancels every unfinished one. Returns how many it cancelled.</summary>
    public int Halt()
    {
        Halted = true;
        var held = _runs.Values.ToList();
        foreach (var run in held)
            run.CancelByCaller();
        return held.Count;
    }

    public void Resume() => Halted = false;

    /// <summary>request/v1 schema, a known preset, and caller blocks whose sha256 matches their text.</summary>
    public (RunRequest? Request, IReadOnlyList<string> Errors) Validate(JsonElement json)
    {
        var errors = ContractSchemas.Validate(ContractSchemas.Request, json);
        if (errors.Count > 0)
            return (null, errors);
        var request = json.Deserialize<RunRequest>(ContractJson.Options)!;
        if (!File.Exists(Path.Combine(presetsDirectory, request.Context.Preset + ".yaml")))
            return (null, [$"unknown preset '{request.Context.Preset}'"]);
        var bad = (request.CallerBlocks ?? []).Where(b => PromptBlock.Hash(b.Text) != b.Sha256)
            .Select(b => $"caller block '{b.Name}': sha256 does not match its text").ToList();
        return bad.Count > 0 ? (null, bad) : (request, []);
    }

    /// <summary>Queues a run, or returns null when <see cref="MaxUnfinished"/> runs are unfinished.</summary>
    /// <param name="chargeTo">The parent run whose token started this one: it runs on the child gate and its cost goes to that run's ledger.</param>
    public RunHandle? Start(RunRequest request, string? parentRunId = null, string? chargeTo = null)
    {
        // ponytail: count-then-add is not atomic; two racing calls can exceed the bound by one.
        if (_runs.Count >= MaxUnfinished)
            return null;
        var run = new RunHandle(Orchestrator.NewRunId(), stopping);
        _runs[run.Id] = run;
        run.Publish(RunStatus.Of(run.Id, RunState.Queued, RunEventKind.Accepted));
        _ = Task.Run(() => Execute(run, request, parentRunId, chargeTo));
        return run;
    }

    public RunHandle? Find(string runId) => _runs.GetValueOrDefault(runId);

    private async Task Execute(RunHandle run, RunRequest request, string? parentRunId, string? chargeTo)
    {
        var gate = chargeTo is null ? _gate : _childGate;
        try
        {
            await gate.WaitAsync(run.Token);
            try
            {
                // A batch holds one slot of the main gate and schedules its own tasks (ADR 0039); without a driven setup the orchestrator refuses the request.
                var result = request.Driven is not null && driven is not null
                    ? await driven.RunAsync(request, run.Id, MintToken, run.Token, run.Publish, () => run.CancelledByCaller)
                    : await orchestrator.RunAsync(request, run.Token, run.Id, run.Publish, parentRunId, () => run.CancelledByCaller);
                if (chargeTo is not null)
                    Ledger.Charge(chargeTo, result.Usage.Usd ?? 0, result.Usage.Input + result.Usage.Output);
                run.Complete(result);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (run.CancelledByCaller)
        {
            // Cancelled in the queue, or before the run's first log write: nothing ran, but the caller still gets a failed result and the log a record.
            run.Complete(await orchestrator.RecordCancelledAsync(request, run.Id, parentRunId, run.Publish));
        }
        catch (Exception e)
        {
            // Only cancellation reaches here (the orchestrator turns other failures into a failed result).
            run.Fail(e);
        }
        finally
        {
            // The run record is in the log by now; later reads go there.
            _runs.TryRemove(run.Id, out _);
            run.Dispose();
        }
    }
}

/// <summary>A run this process accepted and has not finished: its events so far and its result to come.</summary>
public sealed class RunHandle(string id, CancellationToken stopping) : IDisposable
{
    private readonly CancellationTokenSource _cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
    private readonly List<RunStatus> _events = [];
    private readonly TaskCompletionSource<ResultContract> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Id => id;

    /// <summary>Cancelled by the server's shutdown or by <see cref="CancelByCaller"/>.</summary>
    public CancellationToken Token => _cancel.Token;

    /// <summary>True once a caller cancelled this run on purpose, as opposed to a shutdown.</summary>
    public bool CancelledByCaller { get; private set; }

    public void CancelByCaller()
    {
        CancelledByCaller = true;
        _cancel.Cancel();
    }

    public void Dispose() => _cancel.Dispose();

    public Task<ResultContract> Done => _done.Task;

    /// <summary>The latest event: queued until the run starts, then running, then how it finished.</summary>
    public RunStatus Latest
    {
        get
        {
            lock (_events)
                return _events[^1];
        }
    }

    public IReadOnlyList<RunStatus> History
    {
        get
        {
            lock (_events)
                return [.. _events];
        }
    }

    public void Publish(RunStatus e)
    {
        TaskCompletionSource changed;
        lock (_events)
        {
            _events.Add(e);
            changed = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
    }

    /// <summary>Every event from the first, then each new one, until the run finishes.</summary>
    public async IAsyncEnumerable<RunStatus> Events([EnumeratorCancellation] CancellationToken ct)
    {
        for (var next = 0; ;)
        {
            RunStatus[] batch;
            Task changed;
            lock (_events)
            {
                batch = [.. _events.Skip(next)];
                changed = _changed.Task;
            }
            foreach (var e in batch)
            {
                next++;
                yield return e;
                if (e.Event == RunEventKind.RunFinished)
                    yield break;
            }
            await changed.WaitAsync(ct);
        }
    }

    internal void Complete(ResultContract result) => _done.TrySetResult(result);

    internal void Fail(Exception e)
    {
        Publish(RunStatus.Of(id, RunState.Lost, RunEventKind.RunFinished) with { Detail = e.Message });
        _done.TrySetException(e);
    }
}
