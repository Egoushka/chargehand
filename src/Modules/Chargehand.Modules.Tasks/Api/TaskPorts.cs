using Chargehand.Contracts;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Modules.Tasks.Api;

/// <summary>P3, an upward port (ADR 0060, amendment of 2026-10-14; C3 brief, section 3): the batch's state of one task, which the batch keeps and the task runner reads and writes as the
/// task goes. That is the task's entry in the batch state (ADR 0043), the records kept for the console (CHARGEHAND-214), and the handle that stops its running session. The batch code implements
/// it and the host wires it. It goes when the batch state does (ADR 0061, C4).</summary>
public interface ITaskState
{
    /// <summary>The task's entry; null when the batch keeps no state.</summary>
    TaskEntry? Entry(string taskId);

    void Update(string taskId, Func<TaskEntry, TaskEntry> change);

    /// <summary>Records a task's end, which the batch's scheduler reached after its caps had their say.</summary>
    void Finish(TaskOutcome outcome);

    /// <summary>False: the batch keeps no records, so the task builds none.</summary>
    bool KeepsRecords { get; }

    void Append(SessionRecord record);

    void Append(GovernorRecord record);

    void Append(ChecksRecord record);

    void Append(CiAttemptRecord record);

    /// <summary>How to stop the session the task is waiting on, until <see cref="SessionEnded"/>.</summary>
    void SessionStarted(string taskId, Func<Task> stop);

    void SessionEnded(string taskId);
}

/// <summary>P4, an upward port (ADR 0060, amendment of 2026-10-14; C3 brief, section 3): the task's own <c>result/v1</c>, built from what the task left with its citations checked against the
/// branch commit and run through the support check, then signed. The result and proof code (Research) implements it and the host wires it.</summary>
public interface ITaskResults
{
    Task<ResultContract> BuildAsync(DrivenTaskInput input, CancellationToken ct);

    /// <summary>The result as the server signs it; unchanged where results are not signed.</summary>
    ResultContract Sign(ResultContract result);
}

/// <param name="Session">How the session ended; its usage and questions.</param>
/// <param name="ReportJson">The last fenced JSON block the session printed; null when it printed none.</param>
/// <param name="Handover">Null when the session did not get as far as a handover.</param>
/// <param name="Children">The runs the session started through its token (research, review): their run-store rows.</param>
/// <param name="RepositoryPath">chargehand's scratch clone that holds the branch commit, for the citations.</param>
/// <param name="Priced">The credential has a dollar price (an API key): the result carries dollars; else they are unknown, never 0.</param>
/// <param name="Kept">The work an unfinished task leaves for a continuation (ADR 0045); null: none.</param>
public sealed record DrivenTaskInput(string TaskId, string RunId, string TraceId, string Model, string ClaudeVersion, SessionOutcome Session, string? ReportJson,
    HandoverOutcome? Handover, IReadOnlyList<RunSummary> Children, string RepositoryPath, string BaseCommit, bool Priced, KeptWork? Kept = null);
