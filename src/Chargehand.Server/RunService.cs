using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Kernel;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Tasks.Api;
using Chargehand.RunLog;
using Chargehand.Workspace;
using Microsoft.AspNetCore.Http;

namespace Chargehand.Server;

/// <summary>
/// Runs requests in the background, one at a time unless the profile raises <c>http.max_concurrent_runs</c>: ADR 0011
/// allows 2 concurrent nodes per model, and a split already uses both. A run outlives the HTTP or MCP call that started it. Start and run records live in the run log, which
/// the CLI shares (ADR 0018); events live here only while the run is unfinished.
/// </summary>
/// <param name="accounts">The profile's accounts: which model credential each one's runs use (<see cref="CredentialHealth.KeyOf"/>).</param>
/// <param name="notify">Posts a title and a text to the owner (the schedules' <c>notify_url</c>); null: nowhere.</param>
public sealed class RunService(Orchestrator orchestrator, string presetsDirectory, CancellationToken stopping, DrivenRun? driven = null,
    int maxConcurrentRuns = 1, int maxSessionCalls = 2, string? stateDirectory = null, IRunLog? log = null, TimeProvider? clock = null,
    IReadOnlyList<AccountSettings>? accounts = null, Func<string, string, CancellationToken, Task>? notify = null) : IDisposable, IAsyncDisposable
{
    /// <summary>Unfinished top-level runs held at most; beyond it a new run is refused (HTTP 429). A session's research and review runs do not count: the child gate and the session count bound them.</summary>
    public const int MaxUnfinished = 10;

    private readonly SemaphoreSlim _gate = new(Math.Max(1, maxConcurrentRuns), Math.Max(1, maxConcurrentRuns));

    /// <summary>Runs a driven session starts through its token (research, review) use this gate, not the main one: the batch that holds the main gate
    /// is waiting for them, so sharing it would deadlock (ADR 0039). Its size is <c>driven.max_session_calls</c>, shared by every session.</summary>
    private readonly SemaphoreSlim _childGate = new(Math.Max(1, maxSessionCalls), Math.Max(1, maxSessionCalls));
    private readonly ConcurrentDictionary<string, RunHandle> _runs = new();

    /// <summary>The runs' background work until it ends, so a stop can wait for it.</summary>
    private readonly ConcurrentDictionary<Task, byte> _executing = new();

    public void Dispose()
    {
        _gate.Dispose();
        _childGate.Dispose();
        driven?.Dispose();
    }

    /// <summary>Waits for the runs the host's stop cancelled to write their end, then disposes: the gates and the driven run's journal are still in use until then.</summary>
    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_executing.Keys).ConfigureAwait(false);
        Dispose();
    }

    private void Track(Func<Task> execute)
    {
        var task = Task.Run(execute, CancellationToken.None);
        _executing[task] = 0;
        _ = task.ContinueWith(t => _executing.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public RunTokenService Tokens { get; } = stateDirectory is null ? new() : RunTokenService.LoadOrCreate(Path.Combine(stateDirectory, "run-token.key"));

    public RunTokenLedger Ledger { get; } = new();

    /// <summary>The kill switch is on: no new run starts until <see cref="Resume"/> (ADR 0039).</summary>
    public bool Halted { get; private set; }

    private string MintToken(TaskGrant g, string? actor) =>
        Tokens.Issue(new RunTokenClaims(g.RunId, g.RepositoryPath, g.Commit, g.MaxUsd, g.MaxTokens, g.Expires, actor), DateTimeOffset.UtcNow);

    public const string HaltedMessage = "the server is halted; POST /v1/resume to accept runs again";

    /// <summary>Cancels a run this process holds, on purpose. False when it holds no such run, or <paramref name="who"/> may not see it (ADR 0050).</summary>
    public bool Cancel(string runId, Principal? who = null)
    {
        if (_runs.GetValueOrDefault(runId) is not { } run || !Principal.Sees(who, run.Actor))
            return false;
        run.CancelByCaller();
        return true;
    }

    /// <summary>The time the accounts' windows are cut by.</summary>
    public DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    /// <summary>What an account may start (ADR 0050). The owner, stdio and the CLI are unrestricted. An account's request comes back with its budgets cut to what the window
    /// has left, or refused: a run underway can still overshoot a cap by its own budget.</summary>
    public async Task<(RunRequest Request, AccountRefusal? Refusal)> AdmitAsync(Principal? who, RunRequest request, CancellationToken ct)
    {
        if (Credentials.Paused(CredentialOf(who?.Account?.Name)) is { } paused)
            return (request, new("credential_paused", $"the model credential these runs use was rejected at {paused.Since:u} ({paused.Reason}); runs are admitted again once a probe succeeds",
                StatusCodes.Status503ServiceUnavailable));
        if (who?.Account is not { } account)
            return (request, null);
        var now = Now;
        // Another account's run is not a continuation this account may build on.
        foreach (var from in request.Driven?.Tasks.Select(t => t.ContinueFrom).OfType<ContinueFrom>() ?? [])
            if (log is null || (await log.ReadAsync(from.RunId, ct)).Start?.Actor != account.Name)
                return (request, new("continue_not_allowed", $"continue_from {from.RunId} is not a run of account '{account.Name}'", StatusCodes.Status403Forbidden));
        ActorSpend none = new(0, 0);
        var day = log is not null && AccountPolicy.UsesDay(account) ? await log.SpendAsync(account.Name, AccountPolicy.DayStart(now), ct) : none;
        var month = log is not null && AccountPolicy.UsesMonth(account) ? await log.SpendAsync(account.Name, AccountPolicy.MonthStart(now), ct) : none;
        // ponytail: count-then-start is not atomic; two racing requests can exceed max_concurrent_runs by one.
        var unfinished = Unfinished(account.Name);
        return AccountPolicy.Refusal(account, request, unfinished, day, month, now) is { } refusal
            ? (request, refusal)
            : (AccountPolicy.Clamp(account, request, day, month), null);
    }

    /// <summary>The checkouts this server reads with the commit each has checked out (the repositories tool).</summary>
    public Task<IReadOnlyList<RepositoryHead>> RepositoriesAsync(CancellationToken ct) => orchestrator.RepositoriesAsync(ct);

    /// <summary>Which model credentials are paused because the provider rejected them.</summary>
    public CredentialHealth Credentials { get; } = new();

    /// <summary>The credential a run of <paramref name="actor"/> uses: <see cref="CredentialHealth.Server"/> or the account's own.</summary>
    public string CredentialOf(string? actor) => CredentialHealth.KeyOf(actor, accounts);

    /// <summary>Every credential runs can use: the server's and each account's own.</summary>
    public IReadOnlyList<string> CredentialKeys => [CredentialHealth.Server, .. (accounts ?? []).Where(a => a.Model?.ApiKeySecret is not null).Select(a => a.Name)];

    /// <summary>Pauses or resumes <paramref name="key"/> on what a run or a probe saw, and tells the owner once per change.</summary>
    /// <param name="error">Null: the credential answered.</param>
    public async Task ReportCredentialAsync(string key, string? error, CancellationToken ct)
    {
        if (error is null ? Credentials.Succeed(key) : Credentials.Fail(key, error, Now))
            await NotifyAsync(error is null ? $"chargehand: credential '{key}' works again" : $"chargehand: credential '{key}' rejected, runs paused",
                error is null ? "A probe succeeded; runs on it are admitted again." : $"The provider rejected it: {Credentials.Paused(key)?.Reason}. New runs on it are refused until a probe succeeds.", ct);
    }

    private async Task NotifyAsync(string title, string body, CancellationToken ct)
    {
        if (notify is null)
            return;
        try
        {
            await notify(title, body, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine($"chargehand serve: could not send the notification '{title}': {e.Message}");
        }
    }

    /// <summary>The account's month spend forecast (ADR 0076); null without a run log.</summary>
    public async Task<SpendForecast?> ForecastAsync(AccountSettings account, CancellationToken ct)
    {
        if (log is null)
            return null;
        var now = Now;
        var month = await log.SpendAsync(account.Name, AccountPolicy.MonthStart(now), ct);
        var trailing = await log.SpendAsync(account.Name, SpendForecasts.TrailingStart(now), ct);
        return SpendForecasts.Of(month.Usd, trailing.Usd, account.MaxUsdPerMonth, now);
    }

    /// <summary>Unfinished top-level runs (queued or running) that <paramref name="actor"/> started and this process holds.</summary>
    public int Unfinished(string actor) => _runs.Values.Count(r => !r.IsChild && r.Actor == actor);

    /// <summary>The presets the server accepts: the files in its presets directory.</summary>
    public IReadOnlyList<string> Presets() =>
        Directory.Exists(presetsDirectory) ? [.. Directory.EnumerateFiles(presetsDirectory, "*.yaml").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal)] : [];

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

    /// <summary>Queues a run, or returns null when <see cref="MaxUnfinished"/> top-level runs are unfinished (a run with <paramref name="chargeTo"/> is never refused).</summary>
    /// <param name="chargeTo">The parent run whose token started this one: it runs on the child gate and its cost goes to that run's ledger.</param>
    /// <param name="actor">The account that started it (ADR 0050); on the run's records, result and events.</param>
    public RunHandle? Start(RunRequest request, string? parentRunId = null, string? chargeTo = null, string? actor = null)
    {
        // ponytail: count-then-add is not atomic; two racing calls can exceed the bound by one.
        if (chargeTo is null && _runs.Values.Count(r => !r.IsChild) >= MaxUnfinished)
            return null;
        var run = new RunHandle(RunIds.New(), stopping) { IsChild = chargeTo is not null, ParentRunId = parentRunId, Actor = actor, IsBatch = request.Driven is not null };
        _runs[run.Id] = run;
        run.Publish(RunStatus.Of(run.Id, RunState.Queued, RunEventKind.Accepted));
        Track(() => Execute(run, request, parentRunId, chargeTo));
        return run;
    }

    /// <summary>Takes up the driven batches a restart interrupted, under their own run ids, then sweeps what no batch owns (ADR 0043). Each resumed batch holds a slot of the main gate
    /// and counts as unfinished, as before the restart. Returns the names the sweep removed.</summary>
    public async Task<IReadOnlyList<string>> ResumeAsync(CancellationToken ct)
    {
        if (driven is null)
            return [];
        var open = await driven.UnfinishedAsync(ct);
        // Swept before any batch starts, so a resource a resumed batch makes is never taken for a leftover.
        var swept = await driven.SweepOrphansAsync(open, ct);
        foreach (var state in open)
        {
            var run = new RunHandle(state.RunId, stopping) { Actor = state.Actor, IsBatch = true };
            _runs[run.Id] = run;
            run.Publish(RunStatus.Of(run.Id, RunState.Queued, RunEventKind.Accepted));
            Track(() => Execute(run, state.Request, null, null, state));
        }
        return swept;
    }

    public RunHandle? Find(string runId) => _runs.GetValueOrDefault(runId);

    /// <summary>The runs this process holds (queued or running), sessions' research and review calls included.</summary>
    public IReadOnlyList<RunHandle> Held => [.. _runs.Values];

    /// <summary>How many finished runs' events are kept for the console, and how many events of one run.</summary>
    internal const int ArchivedRuns = 25;
    internal const int ArchivedEvents = 2000;

    private readonly object _archiveLock = new();
    private readonly Dictionary<string, IReadOnlyList<RunStatus>> _archive = [];
    private readonly Queue<string> _archiveOrder = new();

    /// <summary>A run's events: all of them while this process holds it, the last <see cref="ArchivedEvents"/> of each of the last <see cref="ArchivedRuns"/> finished runs after, null otherwise
    /// (a run of another process, or from before a restart: events live in memory only).</summary>
    public IReadOnlyList<RunStatus>? Events(string runId)
    {
        if (_runs.GetValueOrDefault(runId) is { } run)
            return run.History;
        lock (_archiveLock)
            return _archive.GetValueOrDefault(runId);
    }

    private void Archive(RunHandle run)
    {
        var history = run.History;
        if (history.Count > ArchivedEvents)
            history = [.. history.Skip(history.Count - ArchivedEvents)];
        lock (_archiveLock)
        {
            if (_archive.TryAdd(run.Id, history))
                _archiveOrder.Enqueue(run.Id);
            else
                _archive[run.Id] = history;
            while (_archiveOrder.Count > ArchivedRuns)
                _archive.Remove(_archiveOrder.Dequeue());
        }
    }

    private async Task Execute(RunHandle run, RunRequest request, string? parentRunId, string? chargeTo, OpenBatch? resume = null)
    {
        var gate = chargeTo is null ? _gate : _childGate;
        try
        {
            await gate.WaitAsync(run.Token);
            try
            {
                // A batch holds one slot of the main gate and schedules its own tasks (ADR 0039); without a driven setup the orchestrator refuses the request.
                var result = request.Driven is not null && driven is not null
                    ? await driven.RunAsync(request, run.Id, g => MintToken(g, run.Actor), run.Token, run.Publish, () => run.CancelledByCaller, resume, run.Actor)
                    : await orchestrator.RunAsync(request, run.Token, run.Id, run.Publish, parentRunId, () => run.CancelledByCaller, run.Actor);
                if (chargeTo is not null)
                    Ledger.Charge(chargeTo, result.Usage.Usd ?? 0, result.Usage.Input + result.Usage.Output);
                if (CredentialHealth.Rejected(result.Error?.Message))
                    await ReportCredentialAsync(CredentialOf(run.Actor), result.Error!.Message, CancellationToken.None);
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
            run.Complete(await orchestrator.RecordCancelledAsync(request, run.Id, parentRunId, run.Publish, run.Actor));
        }
        catch (Exception e)
        {
            // Only cancellation reaches here (the orchestrator turns other failures into a failed result).
            run.Fail(e);
        }
        finally
        {
            // The run record is in the log by now; later reads go there. A research or review call's events are not worth a slot.
            if (!run.IsChild)
                Archive(run);
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

    /// <summary>The account that started the run (ADR 0050); null for none.</summary>
    public string? Actor { get; init; }

    /// <summary>Started through a session's run token: it runs on the child gate.</summary>
    public bool IsChild { get; init; }

    /// <summary>The run it was started under (its start record's parent); null for none.</summary>
    public string? ParentRunId { get; init; }

    /// <summary>A driven batch: its events can replay from the journal (ADR 0061).</summary>
    public bool IsBatch { get; init; }

    /// <summary>Completes at the next <see cref="Publish"/>.</summary>
    internal Task Changed
    {
        get
        {
            lock (_events)
                return _changed.Task;
        }
    }

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
            _events.Add(e.Actor is null && Actor is not null ? e with { Actor = Actor } : e);
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
