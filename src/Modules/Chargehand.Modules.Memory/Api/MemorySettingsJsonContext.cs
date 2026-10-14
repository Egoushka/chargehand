using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Modules.Memory.Api;

/// <summary>The profile's <c>memory</c> list bound without reflection (ADR 0060), with the profile's options: snake_case, comments skipped, unknown
/// keys ignored. The tool arguments stay <see cref="JsonElement"/>s, as written.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(MemoryProviderSettings))]
[JsonSerializable(typeof(List<MemoryProviderSettings>))]
[JsonSerializable(typeof(IEnumerable<MemoryProviderSettings>))]
[JsonSerializable(typeof(IReadOnlyList<MemoryProviderSettings>))]
public sealed partial class MemorySettingsJsonContext : JsonSerializerContext;
