using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Modules.Handover.Api;

/// <summary><c>driven.integrator</c> bound without reflection (ADR 0060), with the profile's options: snake_case, comments skipped, unknown keys ignored.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(DrivenIntegrator))]
public sealed partial class HandoverSettingsJsonContext : JsonSerializerContext;
