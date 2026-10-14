using System.Text.Json;
using System.Text.Json.Serialization;
using Chargehand.Contracts;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Core.Scheduling;

/// <summary>The scheduler's events as the journal's <c>&lt;batch&gt;/tasks</c> flow keeps them (ADR 0063), beside the task events: <c>operator_cancelled</c> and <c>ended</c>. The payload is a JSON
/// object, snake_case, with the task's id under <c>task</c> and the time the shell decided at under <c>at</c>. A kind this code does not know and a payload that does not parse are skipped.</summary>
internal static class ScheduleEventJson
{
    public const string Cancelled = "operator_cancelled";

    public const string EndedKind = "ended";

    public static (string Kind, string Payload) Encode(string taskId, DateTimeOffset at) =>
        (Cancelled, JsonSerializer.Serialize(new CancelLine(taskId, at), Context.CancelLine));

    public static (string Kind, string Payload) Encode(Ended ended) =>
        (EndedKind, JsonSerializer.Serialize(new EndLine(ended.TaskId, ended.Now, ended.Outcome), Context.EndLine));

    /// <returns>Null for a kind this code does not know or a payload that does not parse.</returns>
    public static ScheduleEvent? Decode(string kind, string payload)
    {
        try
        {
            return kind switch
            {
                Cancelled => JsonSerializer.Deserialize(payload, Context.CancelLine) is { Task.Length: > 0 } c ? new OperatorCancelled(c.Task) : null,
                EndedKind => JsonSerializer.Deserialize(payload, Context.EndLine) is { Task.Length: > 0, Outcome: not null } e ? new Ended(e.Task, e.Outcome, e.At) : null,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ScheduleEventsJsonContext Context => ScheduleEventsJsonContext.Default;
}

/// <summary>An operator's cancel of a task that had not started.</summary>
internal sealed record CancelLine(string Task, DateTimeOffset At);

/// <summary>A started task's end as the scheduler was told it, with the task's and the batch's caps already applied to a final one.</summary>
internal sealed record EndLine(string Task, DateTimeOffset At, TaskOutcome Outcome);

/// <summary>The scheduler's events' JSON without reflection (ADR 0060): snake_case, nulls left out, enums as snake_case strings.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(SnakeCaseEnumConverter<TaskState>)])]
[JsonSerializable(typeof(CancelLine))]
[JsonSerializable(typeof(EndLine))]
internal sealed partial class ScheduleEventsJsonContext : JsonSerializerContext;
