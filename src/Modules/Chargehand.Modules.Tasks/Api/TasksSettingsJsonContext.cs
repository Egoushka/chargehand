using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Modules.Tasks.Api;

/// <summary><c>driven.limits</c> bound without reflection (ADR 0060), with the profile's options: snake_case, comments skipped, unknown keys ignored.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(DrivenLimits))]
public sealed partial class TasksSettingsJsonContext : JsonSerializerContext;
