using System.Collections.Concurrent;
using Chargehand.Modules.Journal.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Api;

/// <summary>The unfinished driven batches of a state directory (ADR 0061, amendments of 2026-10-14): each batch is a step held open in the journal from its start until its flows are forgotten
/// after its run record, with its tasks resolved and planned as two steps whose results are recorded, its tasks' events in its task flow, and its token key in a file of its own. In memory, the
/// batches this process carries, each with its tasks as the journal folds them: what <see cref="Live"/> reads. One process owns the directory.</summary>
public sealed class OpenBatches(string directory, IRunLog log) : IDisposable
{
    /// <summary>Opened with the first batch, or by <see cref="LoadAsync"/> when the directory holds one already.</summary>
    private readonly Lazy<Task<DrivenJournal>> _journal = new(() => DrivenJournal.OpenAsync(directory));

    private readonly ConcurrentDictionary<string, (OpenBatch Batch, TaskJournal Tasks)> _open = new(StringComparer.Ordinal);

    /// <summary>Batches this process began and has not ended: <see cref="LoadAsync"/> leaves them to their runs.</summary>
    private readonly ConcurrentDictionary<string, byte> _begun = new(StringComparer.Ordinal);

    /// <summary>The state directory: the journal, the token key files and, for one release, the snapshots written before the journal held a batch's fields.</summary>
    public string Directory => directory;

    /// <summary>The journal of the batches' side effects (ADR 0061), in <see cref="Directory"/>.</summary>
    public Task<DrivenJournal> JournalAsync() => _journal.Value;

    /// <summary>The unfinished batches this process carries, oldest first, with their tasks as the journal folds them now.</summary>
    public IReadOnlyList<OpenBatch> Live => [.. _open.Values.Select(o => o.Batch with { Tasks = o.Tasks.Entries }).OrderBy(b => b.Started).ThenBy(b => b.RunId, StringComparer.Ordinal)];

    /// <summary>The task journal of a batch this process carries; null for any other.</summary>
    public TaskJournal? TasksOf(string batch) => _open.TryGetValue(batch, out var open) ? open.Tasks : null;

    /// <summary>The batch as this process carries it, with its tasks as folded now; null for any other.</summary>
    public OpenBatch? Find(string batch) => _open.TryGetValue(batch, out var open) ? open.Batch with { Tasks = open.Tasks.Entries } : null;

    /// <summary>Reads back the batches a restart interrupted: each open <c>batch</c> step whose tasks were planned and whose run record is not written. One whose run record is written, or that
    /// stopped before its tasks were planned, is a leftover (the second was never resumed, as with the snapshot): its flows and key file are deleted. A batch this process began is left alone.
    /// Snapshots written before the journal held a batch's fields are imported first (<see cref="LegacySnapshots"/>).</summary>
    /// <returns>Every batch this process carries now, with its tasks as folded.</returns>
    public async Task<IReadOnlyList<OpenBatch>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(directory, DrivenJournal.FileName)) && !LegacySnapshots.Any(directory))
            return Live;
        var steps = await JournalAsync().ConfigureAwait(false);
        await LegacySnapshots.ImportAsync(directory, log, steps, ct).ConfigureAwait(false);
        foreach (var key in await steps.UnfinishedAsync(ct: ct).ConfigureAwait(false))
        {
            if (key.Name != StepKey.BatchName || _begun.ContainsKey(key.Batch) || _open.ContainsKey(key.Batch))
                continue;
            if (await ReadAsync(steps, key.Batch, ct).ConfigureAwait(false) is { } batch)
                _open[key.Batch] = (batch, await TaskJournal.OpenAsync(steps, key.Batch, batch.Tasks, ct).ConfigureAwait(false));
            else
                await EndAsync(key.Batch).ConfigureAwait(false);
        }
        return Live;
    }

    /// <summary>The batch from its start record and its steps; null when it has a run record, no start record, or no planned tasks.</summary>
    private async Task<OpenBatch?> ReadAsync(DrivenJournal steps, string batch, CancellationToken ct)
    {
        var entry = await log.ReadAsync(batch, ct).ConfigureAwait(false);
        if (entry is not { Run: null, Start: { } start })
            return null;
        var resolved = await steps.ResultAsync(StepKey.Resolve(batch), BatchStepsJsonContext.Default.BatchResolved, ct).ConfigureAwait(false);
        var planned = await steps.ResultAsync(StepKey.Plan(batch), BatchStepsJsonContext.Default.BatchPlanned, ct).ConfigureAwait(false);
        return resolved is null || planned is null ? null : new OpenBatch(batch, start.Started, start.TraceId, start.Request, planned.Tasks, resolved.Unresolved, planned.Plan, start.Actor);
    }

    /// <summary>Opens the batch's step, after its start record is written: from now on a restart finds it.</summary>
    public async Task BeginAsync(string batch, CancellationToken ct)
    {
        _begun[batch] = 0;
        await (await JournalAsync().ConfigureAwait(false)).BeginAsync(StepKey.BatchStep(batch), ct).ConfigureAwait(false);
    }

    /// <summary>The batch's <c>resolve</c> step: <paramref name="resolve"/> once, its result recorded.</summary>
    public async Task<BatchResolved> ResolveAsync(string batch, Func<CancellationToken, Task<BatchResolved>> resolve, CancellationToken ct) =>
        await (await JournalAsync().ConfigureAwait(false)).RunAsync(StepKey.Resolve(batch), (_, token) => resolve(token), BatchStepsJsonContext.Default.BatchResolved, ct).ConfigureAwait(false);

    /// <summary>The batch's <c>plan</c> step: <paramref name="plan"/> once, its result recorded.</summary>
    public async Task<BatchPlanned> PlanAsync(string batch, Func<CancellationToken, Task<BatchPlanned>> plan, CancellationToken ct) =>
        await (await JournalAsync().ConfigureAwait(false)).RunAsync(StepKey.Plan(batch), (_, token) => plan(token), BatchStepsJsonContext.Default.BatchPlanned, ct).ConfigureAwait(false);

    /// <summary>Carries <paramref name="batch"/>, planned: its tasks folded from its task flow over its tasks as planned. A batch carried already keeps its fold.</summary>
    public async Task<TaskJournal> OpenAsync(OpenBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (_open.TryGetValue(batch.RunId, out var open))
            return open.Tasks;
        var tasks = await TaskJournal.OpenAsync(await JournalAsync().ConfigureAwait(false), batch.RunId, batch.Tasks, ct).ConfigureAwait(false);
        _open[batch.RunId] = (batch, tasks);
        return tasks;
    }

    /// <summary>Carries a batch handed in whole, as a restart that did not come through <see cref="LoadAsync"/> does: from the journal when it holds the batch, else with its steps recorded from it
    /// and each task's entry imported into its flow, as for a snapshot (<see cref="LegacySnapshots"/>). A batch carried already keeps its fold.</summary>
    public async Task<TaskJournal> AdoptAsync(OpenBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (_open.TryGetValue(batch.RunId, out var open))
            return open.Tasks;
        _begun[batch.RunId] = 0;
        var steps = await JournalAsync().ConfigureAwait(false);
        if (await ReadAsync(steps, batch.RunId, ct).ConfigureAwait(false) is { } kept)
            return await OpenAsync(kept, ct).ConfigureAwait(false);
        var planned = await LegacySnapshots.RecordAsync(steps, batch, entriesHavePhases: true, ct).ConfigureAwait(false);
        return await OpenAsync(batch with { Tasks = planned }, ct).ConfigureAwait(false);
    }

    /// <summary>Forgets a batch whose run record is written, or that will not be resumed: its task flow and its flow of steps and events, then its key file. A crash before the flows are forgotten
    /// leaves an open step whose run record exists, which the next <see cref="LoadAsync"/> ends again.</summary>
    public async Task EndAsync(string batch)
    {
        var steps = await JournalAsync().ConfigureAwait(false);
        await TaskJournal.ForgetAsync(steps, batch).ConfigureAwait(false);
        await steps.ForgetAsync(batch).ConfigureAwait(false);
        Drop(batch);
    }

    /// <summary>Lets go of a batch without touching the journal: its key file and any snapshot it was imported from are deleted, and this process stops carrying it.</summary>
    public void Drop(string batch)
    {
        TokenKeyFile.Delete(directory, batch);
        LegacySnapshots.Delete(directory, batch);
        _begun.TryRemove(batch, out _);
        if (_open.TryRemove(batch, out var open))
            open.Tasks.Dispose();
    }

    public void Dispose()
    {
        foreach (var open in _open.Values)
            open.Tasks.Dispose();
        _open.Clear();
        if (_journal is { IsValueCreated: true, Value.IsCompletedSuccessfully: true })
            _journal.Value.Result.Dispose();
    }
}
