using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Modules.Runners.Api;

/// <summary><c>driven.admission</c>, <c>driven.runner</c> and <c>driven.runners</c> bound without reflection (ADR 0060), with the profile's options: snake_case, comments skipped, unknown keys ignored.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(DrivenAdmission))]
[JsonSerializable(typeof(DrivenRunner))]
[JsonSerializable(typeof(IReadOnlyList<DrivenRunnerEntry>))]
public sealed partial class RunnersSettingsJsonContext : JsonSerializerContext;
