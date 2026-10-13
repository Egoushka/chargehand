using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Config;

/// <summary>What the governor does with its decisions: <c>off</c> makes none, <c>observe</c> logs them to <c>governor.jsonl</c> and acts on none (ADR 0045's first release),
/// <c>enforce</c> also stops the session on a checkpoint or a stop.</summary>
[JsonConverter(typeof(SnakeCaseEnum<GovernorMode>))]
public enum GovernorMode { Off, Observe, Enforce }

/// <summary>Writes an enum as its snake_case name wherever it is serialized: <c>task.json</c>, <c>session-outcome.json</c> and <c>governor.jsonl</c> use different options.</summary>
public sealed class SnakeCaseEnum<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.SnakeCaseLower) where T : struct, Enum;
