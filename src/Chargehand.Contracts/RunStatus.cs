namespace Chargehand.Contracts;

/// <summary>run-status/v1: a run that has not finished (HTTP 202 and 410 bodies), and the data of each progress event.</summary>
public sealed record RunStatus(
    string ContractVersion,
    string RunId,
    RunState Status,
    RunEventKind? Event = null,
    DateTimeOffset? At = null,
    string? Action = null,
    string? ExecutedAction = null,
    int? Nodes = null,
    string? NodeId = null,
    ResultStatus? NodeStatus = null,
    decimal? Usd = null,
    string? Detail = null,
    ResultContract? Result = null)
{
    public static RunStatus Of(string runId, RunState status, RunEventKind? kind = null) => new("run-status/v1", runId, status, kind, DateTimeOffset.UtcNow);

    /// <summary>The run state a finished run's result status maps to.</summary>
    public static RunState StateOf(ResultStatus status) => Enum.Parse<RunState>(status.ToString());
}

/// <summary>Lost: the process that ran it ended before it finished. The last four mirror result/v1's status.</summary>
public enum RunState { Queued, Running, Lost, Completed, NeedsInput, Failed, Denied }

public enum RunEventKind { Accepted, Started, Intake, NodeStarted, NodeFinished, RunFinished }
