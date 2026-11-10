using System.Text.Json.Serialization;

namespace Chargehand.Modules.Accounts.Api;

/// <summary>The run-approval file without reflection (ADR 0060), written as before: snake_case, indented.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(RunApprovalGate.Stored))]
internal sealed partial class RunApprovalJsonContext : JsonSerializerContext;
