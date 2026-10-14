using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Config;

/// <summary>profile/v1 bound without reflection (ADR 0060): the root and the sections <c>Config</c> still declares. In <see cref="Profile.LoadJson"/>
/// each module's own context comes first and supplies that module's sections.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(Profile))]
internal sealed partial class ProfileJsonContext : JsonSerializerContext;
