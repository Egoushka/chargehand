using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Modules.Oversight.Api;

/// <summary><c>driven.governor</c> and <c>driven.learning</c> bound without reflection (ADR 0060), with the profile's options: snake_case, comments skipped, unknown keys ignored.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(DrivenGovernor))]
[JsonSerializable(typeof(DrivenLearning))]
public sealed partial class OversightSettingsJsonContext : JsonSerializerContext;
