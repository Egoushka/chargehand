using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Config;

/// <summary>Writes an enum as its snake_case name wherever it is serialized: <c>task.json</c>, <c>session-outcome.json</c> and <c>governor.jsonl</c> use different options.</summary>
public sealed class SnakeCaseEnum<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.SnakeCaseLower) where T : struct, Enum;
