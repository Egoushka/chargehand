using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Chargehand.Contracts;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Tasks.Core;

/// <summary>A task event as the journal's <c>&lt;batch&gt;/tasks</c> flow keeps it (ADR 0061, amendment of 2026-10-14): the kind names the event, and the payload is a JSON object with the task's id
/// under <c>task</c> beside the event's own fields, snake_case. A kind this code does not know, a payload that does not parse and a member it does not know are skipped, so a later writer can add
/// its own kinds to the flow.</summary>
internal static class TaskEventJson
{
    public static (string Kind, string Payload) Encode(string taskId, TaskEvent e)
    {
        var (kind, node) = e switch
        {
            TaskPlaced x => ("placed", Node(x, Context.TaskPlaced)),
            TaskBegan x => ("began", Node(x, Context.TaskBegan)),
            TaskRunRenamed x => ("run_renamed", Node(x, Context.TaskRunRenamed)),
            TaskSessionStarting x => ("session_starting", Node(x, Context.TaskSessionStarting)),
            TaskContainerStarted x => ("container_started", Node(x, Context.TaskContainerStarted)),
            TaskSessionRead x => ("session_read", Node(x, Context.TaskSessionRead)),
            TaskContinuationBegan x => ("continuation_began", Node(x, Context.TaskContinuationBegan)),
            TaskContinuationFailed x => ("continuation_failed", Node(x, Context.TaskContinuationFailed)),
            TaskFinished x => ("finished", Node(x, Context.TaskFinished)),
            TaskReplaced x => ("replaced", Node(x, Context.TaskReplaced)),
            TaskImported x => ("imported", Node(x, Context.TaskImported)),
            _ => throw new InvalidOperationException($"no journal kind for {e.GetType().Name}"),
        };
        node.Insert(0, "task", taskId);
        return (kind, node.ToJsonString());
    }

    /// <returns>Null for a kind this code does not know or a payload that does not parse.</returns>
    public static (string TaskId, TaskEvent Event)? Decode(string kind, string payload)
    {
        try
        {
            if (JsonNode.Parse(payload) is not JsonObject node || node["task"]?.GetValue<string>() is not { Length: > 0 } task)
                return null;
            return Of(kind, node) is { } e ? (task, e) : null;
        }
        catch (Exception x) when (x is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static TaskEvent? Of(string kind, JsonObject node) => kind switch
    {
        "placed" => node.Deserialize(Context.TaskPlaced),
        "began" => node.Deserialize(Context.TaskBegan),
        "run_renamed" => node.Deserialize(Context.TaskRunRenamed),
        "session_starting" => node.Deserialize(Context.TaskSessionStarting),
        "container_started" => node.Deserialize(Context.TaskContainerStarted),
        "session_read" => node.Deserialize(Context.TaskSessionRead),
        "continuation_began" => node.Deserialize(Context.TaskContinuationBegan),
        "continuation_failed" => node.Deserialize(Context.TaskContinuationFailed),
        "finished" => node.Deserialize(Context.TaskFinished),
        "replaced" => node.Deserialize(Context.TaskReplaced),
        "imported" => node.Deserialize(Context.TaskImported),
        _ => null,
    };

    private static JsonObject Node<T>(T e, JsonTypeInfo<T> info) => JsonSerializer.SerializeToNode(e, info)!.AsObject();

    private static TaskEventsJsonContext Context => TaskEventsJsonContext.Default;
}

/// <summary>The task events' JSON without reflection (ADR 0060): snake_case, nulls left out, enums as snake_case strings.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(SnakeCaseEnumConverter<TaskPhase>), typeof(SnakeCaseEnumConverter<TaskState>), typeof(SnakeCaseEnumConverter<SessionStatus>)])]
[JsonSerializable(typeof(TaskPlaced))]
[JsonSerializable(typeof(TaskBegan))]
[JsonSerializable(typeof(TaskRunRenamed))]
[JsonSerializable(typeof(TaskSessionStarting))]
[JsonSerializable(typeof(TaskContainerStarted))]
[JsonSerializable(typeof(TaskSessionRead))]
[JsonSerializable(typeof(TaskContinuationBegan))]
[JsonSerializable(typeof(TaskContinuationFailed))]
[JsonSerializable(typeof(TaskFinished))]
[JsonSerializable(typeof(TaskReplaced))]
[JsonSerializable(typeof(TaskImported))]
internal sealed partial class TaskEventsJsonContext : JsonSerializerContext;
