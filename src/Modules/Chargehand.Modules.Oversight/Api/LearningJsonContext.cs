using System.Text.Json.Serialization;
using Chargehand.Contracts;
using Chargehand.Modules.Forge.Api;

namespace Chargehand.Modules.Oversight.Api;

/// <summary>The lines of <c>learning.jsonl</c>, without reflection (ADR 0060), written byte for byte as the contract options wrote them: snake_case, nulls left out, enums as
/// snake_case strings.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(SnakeCaseEnumConverter<ForgeKind>)])]
[JsonSerializable(typeof(LearningRecord))]
[JsonSerializable(typeof(PrOutcomeRecord))]
[JsonSerializable(typeof(AutopsyRecord))]
internal sealed partial class LearningJsonContext : JsonSerializerContext;
