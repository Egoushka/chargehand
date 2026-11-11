using System.Text.Json.Serialization;

namespace Chargehand.Modules.Accounts.Api;

/// <summary>The claims inside a run token without reflection (ADR 0060), written as before: snake_case, nulls kept.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RunTokenClaims))]
internal sealed partial class RunTokenJsonContext : JsonSerializerContext;
