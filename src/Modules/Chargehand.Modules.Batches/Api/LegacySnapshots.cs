using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Journal.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Api;

// The importer for batches in flight at the deploy that took the batch's fields out of the snapshot (2026-10-14): remove it, with LegacySnapshot, TokenKeyFile.Import, the snapshot's task_flow
// and TaskJournal.ImportAsync, in the release after the one that ships it; a snapshot left then is unreadable.

/// <summary>A batch's snapshot as written before the journal held its fields (ADR 0043): one JSON file per batch in the state directory. <paramref name="TaskFlow"/> null: written before the task
/// journal too, so each task carries its phase.</summary>
internal sealed record LegacySnapshot(string RunId, DateTimeOffset Started, string TraceId, RunRequest Request, string? TokenKey, IReadOnlyList<TaskEntry> Tasks, IReadOnlyList<TaskOutcome>? Unresolved,
    BatchPlan? Plan = null, string? Actor = null, string? TaskFlow = null);

/// <summary>One release only: carries the snapshots of batches in flight at the deploy into the journal (ADR 0061, amendment of 2026-10-14, the second). For each snapshot whose batch has no run
/// record, the start record is written if the run log lacks it, the key goes to the batch's key file, the <c>batch</c> step is opened, the <c>resolve</c> and <c>plan</c> steps are recorded
/// from it, and, for a snapshot from before the task journal, each task's entry is imported into the task flow; then the snapshot is kept as <c>&lt;batch&gt;.imported</c> until the batch ends.
/// Each part can be cut and repeated: the steps that completed keep their result, the key file is written once, and an imported entry stands for the whole entry.</summary>
internal static class LegacySnapshots
{
    private const string Extension = ".json";
    private const string ImportedExtension = ".imported";

    public static bool Any(string directory) => System.IO.Directory.Exists(directory) && System.IO.Directory.EnumerateFiles(directory, "*" + Extension).Any();

    public static async Task ImportAsync(string directory, IRunLog log, DrivenJournal steps, CancellationToken ct)
    {
        if (!System.IO.Directory.Exists(directory))
            return;
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*" + Extension).ToList())
        {
            // A file that cannot be read is skipped: the batch it belonged to stays lost, as before.
            if (Read(file) is not { } snapshot)
                continue;
            var entry = await log.ReadAsync(snapshot.RunId, ct).ConfigureAwait(false);
            if (entry.Run is not null)
            {
                await TaskJournal.ForgetAsync(steps, snapshot.RunId, ct).ConfigureAwait(false);
                await steps.ForgetAsync(snapshot.RunId, ct).ConfigureAwait(false);
                TokenKeyFile.Delete(directory, snapshot.RunId);
                Delete(directory, snapshot.RunId);
                continue;
            }
            if (entry.Start is null)
                await log.AppendAsync(new StartRecord(snapshot.RunId, snapshot.Started, snapshot.TraceId, snapshot.Request, Environment.ProcessId, BootId: ProcessBoot.Id, Actor: snapshot.Actor), ct)
                    .ConfigureAwait(false);
            if (snapshot.TokenKey is { Length: > 0 } key)
                TokenKeyFile.Import(directory, snapshot.RunId, key);
            var batch = new OpenBatch(snapshot.RunId, snapshot.Started, snapshot.TraceId, snapshot.Request, snapshot.Tasks, snapshot.Unresolved ?? [], snapshot.Plan, snapshot.Actor);
            await RecordAsync(steps, batch, entriesHavePhases: snapshot.TaskFlow is null, ct).ConfigureAwait(false);
            Keep(file, Path.Combine(directory, snapshot.RunId + ImportedExtension));
        }
    }

    /// <summary>Records a batch's steps from what it holds: the <c>batch</c> step opened, <c>resolve</c> and <c>plan</c> with the tasks as planned; with <paramref name="entriesHavePhases"/>, each
    /// task's entry imported into the task flow. A step that completed before keeps its result.</summary>
    /// <returns>The tasks as planned.</returns>
    public static async Task<IReadOnlyList<TaskEntry>> RecordAsync(DrivenJournal steps, OpenBatch batch, bool entriesHavePhases, CancellationToken ct)
    {
        IReadOnlyList<TaskEntry> planned = [.. batch.Tasks.Select(TaskJournal.Planned)];
        await steps.BeginAsync(StepKey.BatchStep(batch.RunId), ct).ConfigureAwait(false);
        await steps.RunAsync(StepKey.Resolve(batch.RunId), (_, _) => Task.FromResult(new BatchResolved(planned, batch.Unresolved)), BatchStepsJsonContext.Default.BatchResolved, ct)
            .ConfigureAwait(false);
        var recorded = await steps.RunAsync(StepKey.Plan(batch.RunId), (_, _) => Task.FromResult(new BatchPlanned(planned, batch.Plan)), BatchStepsJsonContext.Default.BatchPlanned, ct)
            .ConfigureAwait(false);
        if (entriesHavePhases)
            await TaskJournal.ImportAsync(steps, batch.RunId, batch.Tasks, ct).ConfigureAwait(false);
        return recorded.Tasks;
    }

    /// <summary>Deletes the snapshot of a batch and the copy kept of it.</summary>
    public static void Delete(string directory, string batch)
    {
        File.Delete(Path.Combine(directory, batch + Extension));
        File.Delete(Path.Combine(directory, batch + ImportedExtension));
    }

    /// <summary>The snapshot is kept under <paramref name="kept"/>; one kept there already, the snapshot as the release before the task journal wrote it, stays as it is.</summary>
    private static void Keep(string file, string kept)
    {
        if (File.Exists(kept))
            File.Delete(file);
        else
            File.Move(file, kept);
    }

    private static LegacySnapshot? Read(string file)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file), BatchStepsJsonContext.Default.LegacySnapshot) is { RunId: { Length: > 0 }, Tasks: not null, Request: not null } snapshot
                ? snapshot : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }
}
