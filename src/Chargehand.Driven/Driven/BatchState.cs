using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Journal.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Driven;

/// <summary>What a restarted server needs to carry on a driven batch (ADR 0043): the request, the tasks as resolved and planned, and the key of the batch's model-token exchange. Where each task
/// stands is the journal's (<see cref="TaskJournal"/>, ADR 0061, amendment of 2026-10-14). The names of the batch's network and egress container follow from <paramref name="RunId"/>; the real model
/// credential is never kept, the profile supplies it again.</summary>
/// <param name="Tasks">The tasks as the batch resolved and planned them (<see cref="TaskJournal.Planned"/>); as read back by <see cref="DrivenRun.UnfinishedAsync"/>, with the journal's entries.</param>
/// <param name="TokenKey">The egress container's key for run tokens; null when the batch has no token exchange.</param>
/// <param name="Unresolved">Tasks that did not resolve to a goal: final from the start.</param>
/// <param name="Plan">What the planning step did (ADR 0049), kept so a resumed batch reports it and does not plan again; null: no planning ran.</param>
/// <param name="Actor">The account that started the batch (ADR 0050); a resumed batch runs as it.</param>
/// <param name="TaskFlow">The journal flow that holds the tasks' events (<see cref="TaskJournal.FlowOf"/>). Null: a snapshot written before the task journal, whose tasks carry their phases and are
/// imported into the flow once (<see cref="BatchStateStore.ImportTasksAsync"/>).</param>
public sealed record BatchState(string RunId, DateTimeOffset Started, string TraceId, RunRequest Request, string? TokenKey, IReadOnlyList<TaskEntry> Tasks, IReadOnlyList<TaskOutcome> Unresolved,
    BatchPlan? Plan = null, string? Actor = null, string? TaskFlow = null)
{
    /// <summary>Every run id the batch's resources can carry: the batch's own and its tasks', every session of a task included (an earlier session's output volume holds the bundle a restart may need).</summary>
    public IEnumerable<string> RunIds => Tasks.Select(t => t.RunId).OfType<string>().Concat(Tasks.SelectMany(t => t.Runs ?? [])).Prepend(RunId);
}

/// <summary>One JSON file per unfinished driven batch, next to the run log (ADR 0043). A write goes to a temporary file that is flushed to disk and then renamed over the old one,
/// so a crash leaves the previous state or the new one, never half of it. The file holds the batch's token key, so it is created readable by its owner only.</summary>
public sealed class BatchStateStore(string directory)
{
    private const string Extension = ".json";
    private const string ImportedExtension = ".imported";

    public void Save(BatchState state)
    {
        Directory.CreateDirectory(directory);
        var target = PathOf(state.RunId);
        var temp = $"{target}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new FileStream(temp, options))
        {
            JsonSerializer.Serialize(file, state, ContractJson.Options);
            file.Flush(flushToDisk: true);
        }
        File.Move(temp, target, overwrite: true);
    }

    public void Delete(string runId)
    {
        File.Delete(PathOf(runId));
        File.Delete(PathOf(runId, ImportedExtension));
    }

    /// <summary>The states on disk. A file that cannot be read is skipped: the batch it belonged to stays <c>lost</c>, as before.</summary>
    public IReadOnlyList<BatchState> List()
    {
        if (!Directory.Exists(directory))
            return [];
        List<BatchState> states = [];
        foreach (var file in Directory.EnumerateFiles(directory, "*" + Extension))
        {
            try
            {
                if (JsonSerializer.Deserialize<BatchState>(File.ReadAllText(file), ContractJson.Options) is { RunId: { Length: > 0 }, Tasks: not null, Request: not null } state)
                    states.Add(state);
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
            }
        }
        return states;
    }

    private string PathOf(string runId, string extension = Extension) => Path.Combine(directory, runId + extension);

    // The importer for batches in flight at the deploy that brought the task journal (2026-10-14): remove it, with ImportedExtension and TaskJournal.ImportAsync, in the release after the one that ships it.

    /// <summary>Carries a snapshot written before the task journal into it (ADR 0061, amendment of 2026-10-14): the snapshot is kept as <c>&lt;batch&gt;.imported</c> until the batch ends, each task's
    /// entry goes into the batch's task flow as one <c>imported</c> event, and the snapshot is saved again with the tasks as planned and <see cref="BatchState.TaskFlow"/> set. Every step can be cut and
    /// repeated: the kept file is written once and read on a repeat, and the flow holds nothing but imported entries until the snapshot is saved.</summary>
    /// <returns>The snapshot as saved; <paramref name="state"/> itself when it needs no import.</returns>
    public async Task<BatchState> ImportTasksAsync(DrivenJournal journal, BatchState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(state);
        if (state.TaskFlow is not null)
            return state;
        var kept = PathOf(state.RunId, ImportedExtension);
        if (!File.Exists(kept))
        {
            var temp = $"{kept}.{Guid.NewGuid():N}.tmp";
            File.Copy(PathOf(state.RunId), temp);
            File.Move(temp, kept);
        }
        var before = JsonSerializer.Deserialize<BatchState>(File.ReadAllText(kept), ContractJson.Options) ?? state;
        await TaskJournal.ImportAsync(journal, state.RunId, before.Tasks, ct);
        var imported = state with { Tasks = [.. before.Tasks.Select(TaskJournal.Planned)], TaskFlow = TaskJournal.FlowOf(state.RunId) };
        Save(imported);
        return imported;
    }
}
