using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Contracts;

/// <summary>
/// The contract types' JSON without reflection, for hosts published with Native AOT (ADR 0060). Writes the same
/// bytes as <see cref="ContractJson.Options"/>: snake_case names, nulls left out, enums as snake_case strings
/// (each enum carries <see cref="SnakeCaseEnumConverter{TEnum}"/>).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RunRequest))]
[JsonSerializable(typeof(TaskSpec))]
[JsonSerializable(typeof(ResultContract))]
[JsonSerializable(typeof(RunStatus))]
[JsonSerializable(typeof(RunSummary))]
public sealed partial class ContractJsonContext : JsonSerializerContext;

/// <summary>An enum as a snake_case string, without the reflection the non-generic converter needs.</summary>
public sealed class SnakeCaseEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.SnakeCaseLower)
    where TEnum : struct, Enum;
