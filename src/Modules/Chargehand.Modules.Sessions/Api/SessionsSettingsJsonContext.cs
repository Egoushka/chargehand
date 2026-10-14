using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Modules.Sessions.Api;

/// <summary><c>driven.services</c> bound without reflection (ADR 0060), with the profile's options: snake_case, comments skipped, unknown keys ignored.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(DrivenSharedServices))]
public sealed partial class SessionsSettingsJsonContext : JsonSerializerContext;
