using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Modules.Batches.Api;

/// <summary><c>driven.planning</c> bound without reflection (ADR 0060), with the profile's options: snake_case, comments skipped, unknown keys ignored.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(DrivenPlanning))]
public sealed partial class BatchesSettingsJsonContext : JsonSerializerContext;
