using System.Text.Json;
using Chargehand.Contracts;

namespace Chargehand.Driven;

/// <summary>Where a task of a batch stands (ADR 0043). <c>Started</c>: the run id, branch and container are fixed; <c>Finished</c>: the session ended and its output was read, the handover may be partial;
/// <c>HandedOver</c>: a pull request opened; <c>Failed</c>: ended without one. The last two carry the task's outcome.</summary>
public enum TaskPhase { Pending, Started, Finished, HandedOver, Failed }

/// <param name="Continuation">How many continuation sessions have started (ADR 0044); <c>RunId</c> and <c>Container</c> are then the latest session's.</param>
/// <param name="Prior">The earlier sessions' usage added up; null before the first continuation.</param>
/// <param name="Runs">Run ids of all the task's sessions, the first one's first.</param>
/// <param name="SessionStarted">When the latest session started: its wall clock counts from here, while <c>Started</c> is the task's.</param>
/// <param name="Best">The latest session whose bundle holds commits: what a continuation is seeded from and a partial handover hands over.</param>
/// <param name="SessionGoal">The latest session's prompt when it is a continuation's (the goal plus the note); null: the task's goal.</param>
/// <param name="StartDetail">The <c>container_started</c> text of a continuation, kept so a continuation redone after a restart says the same.</param>
/// <param name="From">The earlier batch's task this one continues (ADR 0045); the goal above already carries its questions and the answers.</param>
/// <param name="DependsOn">The tasks this one waits for (ADR 0049), as the batch runs them: a planned chain's links included.</param>
/// <param name="Size">The size the governor holds the task to, after planning; null: the request's, else M.</param>
/// <param name="SplitFrom">The task the planning step split this one out of.</param>
public sealed record TaskEntry(string Id, string? Ref, string Goal, TaskPhase Phase = TaskPhase.Pending, string? RunId = null, string? Branch = null, string? Container = null,
    DateTimeOffset? Started = null, TaskOutcome? Outcome = null, int Continuation = 0, SessionOutcome? Prior = null, IReadOnlyList<string>? Runs = null, DateTimeOffset? SessionStarted = null,
    BestBundle? Best = null, string? SessionGoal = null, string? StartDetail = null, ContinuedFrom? From = null, IReadOnlyList<string>? DependsOn = null, string? Size = null, string? SplitFrom = null);

/// <param name="Commits">Commits past the base the bundle holds; a continuation has to add to them to go on.</param>
public sealed record BestBundle(string RunId, int Commits);

/// <summary>What a restarted server needs to carry on a driven batch (ADR 0043): the request, the tasks as resolved, each task's phase, and the key of the batch's model-token exchange.
/// The names of the batch's network and egress container follow from <paramref name="RunId"/>; the real model credential is never kept, the profile supplies it again.</summary>
/// <param name="TokenKey">The egress container's key for run tokens; null when the batch has no token exchange.</param>
/// <param name="Unresolved">Tasks that did not resolve to a goal: final from the start.</param>
/// <param name="Plan">What the planning step did (ADR 0049), kept so a resumed batch reports it and does not plan again; null: no planning ran.</param>
public sealed record BatchState(string RunId, DateTimeOffset Started, string TraceId, RunRequest Request, string? TokenKey, IReadOnlyList<TaskEntry> Tasks, IReadOnlyList<TaskOutcome> Unresolved,
    BatchPlan? Plan = null)
{
    /// <summary>Every run id the batch's resources can carry: the batch's own and its tasks', every session of a task included (an earlier session's output volume holds the bundle a restart may need).</summary>
    public IEnumerable<string> RunIds => Tasks.Select(t => t.RunId).OfType<string>().Concat(Tasks.SelectMany(t => t.Runs ?? [])).Prepend(RunId);
}

/// <summary>One JSON file per unfinished driven batch, next to the run log (ADR 0043). A write goes to a temporary file that is flushed to disk and then renamed over the old one,
/// so a crash leaves the previous state or the new one, never half of it. The file holds the batch's token key, so it is created readable by its owner only.</summary>
public sealed class BatchStateStore(string directory)
{
    private const string Extension = ".json";

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

    public void Delete(string runId) => File.Delete(PathOf(runId));

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

    private string PathOf(string runId) => Path.Combine(directory, runId + Extension);
}

/// <summary>A running batch's state, saved at each change. Tasks run in parallel, so changes are made one at a time.</summary>
public sealed class BatchJournal
{
    private readonly BatchStateStore _store;
    private readonly object _gate = new();
    private BatchState _state;

    public BatchJournal(BatchStateStore store, BatchState state)
    {
        _store = store;
        _state = state;
        Save();
    }

    public TaskEntry? Entry(string taskId)
    {
        lock (_gate)
            return _state.Tasks.FirstOrDefault(t => t.Id == taskId);
    }

    public void Update(string taskId, Func<TaskEntry, TaskEntry> change)
    {
        lock (_gate)
        {
            _state = _state with { Tasks = [.. _state.Tasks.Select(t => t.Id == taskId ? change(t) : t)] };
            Save();
        }
    }

    /// <summary>Records a task's end, which the batch's scheduler reached after its caps had their say.</summary>
    public void Finish(TaskOutcome outcome) =>
        Update(outcome.Id, t => t with { Phase = outcome.State == TaskState.Completed ? TaskPhase.HandedOver : TaskPhase.Failed, Outcome = outcome });

    private void Save() => _store.Save(_state);
}
