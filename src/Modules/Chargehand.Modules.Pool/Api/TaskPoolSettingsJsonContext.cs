using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Modules.Pool.Api;

/// <summary>Source-generated JSON for <see cref="TaskPoolSettings"/> (the profile's <c>http.pool</c>).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(TaskPoolSettings))]
public sealed partial class TaskPoolSettingsJsonContext : JsonSerializerContext;
