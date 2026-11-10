using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Kernel;

/// <summary><c>secrets</c> and <c>telemetry</c> bound without reflection (ADR 0060), with the profile's options: snake_case, comments skipped, unknown keys ignored.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(IReadOnlyList<SecretSource>))]
[JsonSerializable(typeof(TelemetrySettings))]
public sealed partial class KernelSettingsJsonContext : JsonSerializerContext;
