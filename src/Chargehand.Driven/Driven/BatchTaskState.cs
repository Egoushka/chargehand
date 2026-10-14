using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Driven;

/// <summary>P3 (<see cref="ITaskState"/>) over what a batch keeps of its tasks: the batch state (ADR 0043), the records for the console (CHARGEHAND-214) and the operator's controls. Any of the
/// three may be absent, and then its calls do nothing. ADR 0061 retires it with the batch state (C4).</summary>
public sealed class BatchTaskState(string batchId, BatchJournal? journal, BatchRecords? records, BatchControl? control) : ITaskState
{
    public TaskEntry? Entry(string taskId) => journal?.Entry(taskId);

    public void Update(string taskId, Func<TaskEntry, TaskEntry> change) => journal?.Update(taskId, change);

    public void Finish(TaskOutcome outcome) => journal?.Finish(outcome);

    public bool KeepsRecords => records is not null;

    public void Append(SessionRecord record) => records?.Append(batchId, record);

    public void Append(GovernorRecord record) => records?.Append(batchId, record);

    public void Append(ChecksRecord record) => records?.Append(batchId, record);

    public void Append(CiAttemptRecord record) => records?.Append(batchId, record);

    public void SessionStarted(string taskId, Func<Task> stop) => control?.SessionStarted(taskId, stop);

    public void SessionEnded(string taskId) => control?.SessionEnded(taskId);
}
