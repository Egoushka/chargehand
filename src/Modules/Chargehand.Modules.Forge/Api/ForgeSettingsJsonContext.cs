using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Modules.Forge.Api;

/// <summary><c>driven.forge</c> bound without reflection (ADR 0060), with the profile's options: snake_case, comments skipped, unknown keys ignored.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(DrivenForge))]
public sealed partial class ForgeSettingsJsonContext : JsonSerializerContext;
