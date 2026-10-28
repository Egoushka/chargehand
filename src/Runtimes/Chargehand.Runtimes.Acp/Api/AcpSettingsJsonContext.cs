using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Runtimes.Acp.Api;

/// <summary><c>acp</c> bound without reflection (ADR 0060), with the profile's options: snake_case, comments skipped, unknown keys ignored.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(AcpSettings))]
public sealed partial class AcpSettingsJsonContext : JsonSerializerContext;
