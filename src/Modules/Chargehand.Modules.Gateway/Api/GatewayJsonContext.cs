using System.Text.Json.Serialization;

namespace Chargehand.Modules.Gateway.Api;

/// <summary>One line of the egress proxy's log on stderr.</summary>
internal sealed record EgressLogLine(DateTimeOffset At, string? Run, string Host, int Port, bool Allowed, string Reason);

/// <summary>One line of the model endpoint's log on stderr.</summary>
internal sealed record GatewayLogLine(DateTimeOffset At, string? ModelRun, int Status, long Tokens, string Reason);

/// <summary>The body of a refused model call, in the API's own error shape.</summary>
internal sealed record GatewayError(string Type, GatewayErrorDetail Error);

internal sealed record GatewayErrorDetail(string Type, string Message);

/// <summary>The gateway's JSON without reflection (ADR 0060). The names are the ones the anonymous types wrote, so each line is byte-identical.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(EgressLogLine))]
[JsonSerializable(typeof(GatewayLogLine))]
[JsonSerializable(typeof(GatewayError))]
internal sealed partial class GatewayJsonContext : JsonSerializerContext;
