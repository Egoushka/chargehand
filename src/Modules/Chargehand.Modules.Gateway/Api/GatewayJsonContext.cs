using System.Text.Json.Serialization;

namespace Chargehand.Modules.Gateway.Api;

/// <summary>One line of the egress proxy's log on stderr.</summary>
internal sealed record EgressLogLine(DateTimeOffset At, string? Run, string Host, int Port, bool Allowed, string Reason);

/// <summary>One line of the model endpoint's log on stderr.</summary>
/// <remarks><paramref name="Fallback"/> and <paramref name="Usd"/> are written only for a call made on the fallback key, so every other line is as it was.</remarks>
internal sealed record GatewayLogLine(DateTimeOffset At, string? ModelRun, int Status, long Tokens, string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Fallback = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] decimal Usd = 0);

/// <summary>The body of a refused model call, in the API's own error shape.</summary>
internal sealed record GatewayError(string Type, GatewayErrorDetail Error);

internal sealed record GatewayErrorDetail(string Type, string Message);

/// <summary>The gateway's JSON without reflection (ADR 0060). The names are the ones the anonymous types wrote, so each line is byte-identical.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(EgressLogLine))]
[JsonSerializable(typeof(GatewayLogLine))]
[JsonSerializable(typeof(GatewayError))]
[JsonSerializable(typeof(Dictionary<string, List<LearnedHost>>))]
internal sealed partial class GatewayJsonContext : JsonSerializerContext;
