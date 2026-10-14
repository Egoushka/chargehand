using Chargehand.Modules.Journal.Api;
using Chargehand.Modules.Tasks.Core;

namespace Chargehand.Modules.Tasks.Api;

/// <summary>The tasks of one batch as the journal keeps them (ADR 0061, amendment of 2026-10-14): every change of a task's entry is an event in the batch's flow <c>&lt;batch&gt;/tasks</c>,
/// and the entry is the fold of its events (<see cref="TaskMachine"/>) over the task as the batch resolved and planned it. An event is appended before the entry here changes, so what a reader sees
/// is on disk. The task runners write the run's events, the scheduler the placement, the replace and the end. One writer at a time: appends are made in the order they are asked for.</summary>
public sealed class TaskJournal : IDisposable
{
    private const int Page = 1000;

    private readonly DrivenJournal _journal;
    private readonly string _flow;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly List<TaskEntry> _entries;

    private TaskJournal(DrivenJournal journal, string batch, List<TaskEntry> entries) => (_journal, _flow, _entries) = (journal, FlowOf(batch), entries);

    /// <summary>The journal flow of a batch's task events, beside the batch's own flow of steps and events.</summary>
    public static string FlowOf(string batch) => $"{batch}/tasks";

    /// <summary>The task as the batch resolved and planned it, with nothing of a run: what the snapshot keeps and the events fold over.</summary>
    public static TaskEntry Planned(TaskEntry entry) => TaskMachine.Planned(entry);

    /// <summary>The batch's tasks folded from its flow. <paramref name="tasks"/> are the tasks as planned, in the batch's order; an event for a task not among them is skipped.</summary>
    public static async Task<TaskJournal> OpenAsync(DrivenJournal journal, string batch, IEnumerable<TaskEntry> tasks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        List<TaskEntry> entries = [.. tasks.Select(TaskMachine.Planned)];
        var flow = FlowOf(batch);
        for (long after = 0; ;)
        {
            var events = await journal.ReadEventsAsync(flow, after, Page, ct).ConfigureAwait(false);
            foreach (var e in events)
                if (TaskEventJson.Decode(e.Kind, e.Payload) is var (task, input) && entries.FindIndex(t => t.Id == task) is >= 0 and var i)
                    entries[i] = TaskMachine.Decide(entries[i], input).State;
            if (events.Count < Page)
                return new TaskJournal(journal, batch, entries);
            after = events[^1].Sequence;
        }
    }

    /// <summary>Removes the batch's task events, once its run record is written and nothing will resume it.</summary>
    public static Task ForgetAsync(DrivenJournal journal, string batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return journal.ForgetAsync(FlowOf(batch), ct);
    }

    /// <summary>One release only, for batches in flight at the deploy that brought the task journal (2026-10-14): each task's entry as a snapshot written before it held it, appended as one
    /// <c>imported</c> event per task. Safe to repeat while the flow holds nothing else: an imported entry stands for the whole entry. Remove it in the release after the one that ships it.</summary>
    public static async Task ImportAsync(DrivenJournal journal, string batch, IEnumerable<TaskEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        foreach (var entry in entries)
        {
            var (kind, payload) = TaskEventJson.Encode(entry.Id, new TaskImported(entry));
            await journal.AppendEventAsync(FlowOf(batch), kind, payload, ct).ConfigureAwait(false);
        }
    }

    public void Dispose() => _write.Dispose();

    /// <summary>Every task's entry, in the batch's order.</summary>
    public IReadOnlyList<TaskEntry> Entries
    {
        get
        {
            lock (_gate)
                return [.. _entries];
        }
    }

    /// <summary>The task's entry; null when the batch has no such task.</summary>
    public TaskEntry? Entry(string taskId)
    {
        lock (_gate)
            return _entries.Find(t => t.Id == taskId);
    }

    /// <summary>The outcome of a task that has ended (handed over, or failed with an outcome), which a restart returns as it is; null while it has not.</summary>
    public TaskOutcome? Ended(string taskId) => TaskResume.Decide(Entry(taskId), new ResumeFacts()) is TakeUp { Kind: ResumeKind.Done, Outcome: { } done } ? done : null;

    /// <summary>The scheduler placed the task on <paramref name="runner"/>, before anything starts there (ADR 0058).</summary>
    public Task PlacedAsync(string taskId, string runner) => RecordAsync(taskId, new TaskPlaced(runner));

    /// <summary>The runner could not be reached before the task started there: the task goes back to what planning gave it, to be placed again.</summary>
    public Task ReplacedAsync(string taskId) => RecordAsync(taskId, new TaskReplaced());

    /// <summary>The task's end as the scheduler reached it after its caps had their say, or one that never started (a failed dependency, an operator's cancel).</summary>
    public Task FinishedAsync(TaskOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return RecordAsync(outcome.Id, new TaskFinished(outcome));
    }

    /// <summary>Appends the event and folds it in. A cancel of the caller does not stop it: the change happened, as a saved snapshot did before. A task the batch does not have changes nothing.</summary>
    /// <exception cref="InvalidOperationException">The task's phase does not take the event (<see cref="RefuseTaskEvent"/>); nothing is appended.</exception>
    internal async Task RecordAsync(string taskId, TaskEvent e)
    {
        await _write.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (Entry(taskId) is not { } entry)
                return;
            var (next, commands) = TaskMachine.Decide(entry, e);
            if (commands.OfType<RefuseTaskEvent>().FirstOrDefault() is { } refused)
                throw new InvalidOperationException(refused.Why);
            // ReportTaskEnd is the batch's to carry out (see the command).
            foreach (var journal in commands.OfType<JournalTaskEvent>())
            {
                var (kind, payload) = TaskEventJson.Encode(taskId, journal.Event);
                await _journal.AppendEventAsync(_flow, kind, payload, CancellationToken.None).ConfigureAwait(false);
            }
            lock (_gate)
                _entries[_entries.FindIndex(t => t.Id == taskId)] = next;
        }
        finally
        {
            _write.Release();
        }
    }
}
