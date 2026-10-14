using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Modules.Intake.Api;

/// <summary><c>automation</c> and <c>driven.task_source</c> bound without reflection (ADR 0060), with the profile's options: snake_case, comments skipped, unknown keys ignored.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(AutomationSettings))]
[JsonSerializable(typeof(DrivenTaskSource))]
public sealed partial class IntakeSettingsJsonContext : JsonSerializerContext;
