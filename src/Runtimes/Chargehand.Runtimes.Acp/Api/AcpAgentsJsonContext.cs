using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Runtimes.Acp.Api;

/// <summary><c>acp-agents.json</c> without reflection (ADR 0060): snake_case, comments skipped, unknown keys ignored.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(AcpAgentFile))]
internal sealed partial class AcpAgentsJsonContext : JsonSerializerContext;
