using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Driven;

/// <summary>P3 (<see cref="ITaskState"/>) over the batch state (ADR 0043); with none, its calls do nothing. ADR 0061 retires it with the batch state (C4).</summary>
public sealed class BatchTaskState(BatchJournal? journal) : ITaskState
{
    public TaskEntry? Entry(string taskId) => journal?.Entry(taskId);

    public void Update(string taskId, Func<TaskEntry, TaskEntry> change) => journal?.Update(taskId, change);

    public void Finish(TaskOutcome outcome) => journal?.Finish(outcome);
}
