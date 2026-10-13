using System.Text.Json.Serialization;

namespace Chargehand.Modules.Ledger.Api;

/// <summary>The run log's records without reflection (ADR 0060), written exactly as <c>ContractJson.Options</c> writes them.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StartRecord))]
[JsonSerializable(typeof(CallRecord))]
[JsonSerializable(typeof(RunRecord))]
[JsonSerializable(typeof(ScoreRecord))]
public sealed partial class LedgerJsonContext : JsonSerializerContext;
