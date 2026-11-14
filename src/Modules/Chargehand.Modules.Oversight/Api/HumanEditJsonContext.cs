using System.Text.Json.Serialization;

namespace Chargehand.Modules.Oversight.Api;

/// <summary>The lines of <c>human-edits.jsonl</c> and <c>proposals.jsonl</c>, without reflection (ADR 0060).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HumanEditCommit))]
[JsonSerializable(typeof(ProposalRecord))]
internal sealed partial class HumanEditJsonContext : JsonSerializerContext;
