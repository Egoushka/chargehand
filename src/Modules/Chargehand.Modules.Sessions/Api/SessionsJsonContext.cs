using System.Text.Json.Serialization;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Sessions.Api;

/// <summary>The <c>mcp.json</c> Claude Code reads: the chargehand server over HTTP with the run's token. The names are the ones the file had as an anonymous type.</summary>
internal sealed record McpConfigFile([property: JsonPropertyName("mcpServers")] McpConfigServers McpServers);

internal sealed record McpConfigServers([property: JsonPropertyName("chargehand")] McpConfigServer Chargehand);

internal sealed record McpConfigServer([property: JsonPropertyName("type")] string Type, [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("headers")] McpConfigHeaders Headers);

internal sealed record McpConfigHeaders(string Authorization);

/// <summary>The session's JSON without reflection (ADR 0060). It has no options of its own: each user makes an instance with the options it used before, so every file and line
/// is written as it was.</summary>
[JsonSerializable(typeof(SessionTask))]
[JsonSerializable(typeof(SessionUsage))]
[JsonSerializable(typeof(SessionOutcome))]
[JsonSerializable(typeof(GovernorDecision))]
[JsonSerializable(typeof(ToolchainManifest))]
[JsonSerializable(typeof(McpConfigFile))]
[JsonSerializable(typeof(ReminderHookOutput))]
[JsonSerializable(typeof(ReminderSettings))]
[JsonSerializable(typeof(HandoffBuilder.Document))]
[JsonSerializable(typeof(Dictionary<string, long>))]
[JsonSerializable(typeof(Dictionary<string, decimal>))]
internal sealed partial class SessionsJsonContext : JsonSerializerContext;
