using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Modules.Research.Api;

/// <summary>The hash of a node's tool set, recorded in <c>as_sent.tools_sha256</c>.</summary>
public static class ToolSetHash
{
    /// <summary>Proxy for OpenCode's tool catalog, which is a function of its version, the agent and the ruleset, and of the
    /// MCP tools granted (server, tool names, description hash); the same as without grants when there are none.</summary>
    public static string ToolsSha256(string opencodeVersion, string agent, IReadOnlyList<PermissionRule> rules, IReadOnlyList<ServiceGrant>? grants = null)
    {
        var ruleList = rules.Select(r => new[] { r.Action, r.Resource, r.Effect.ToString() }).ToArray();
        var json = grants is not { Count: > 0 }
            ? JsonSerializer.Serialize(new ToolSetBasis(opencodeVersion, agent, ruleList), ToolSetHashJsonContext.Default.ToolSetBasis)
            : JsonSerializer.Serialize(
                new ToolSetBasisWithServices(opencodeVersion, agent, ruleList, grants.Select(g => new[] { g.Server, string.Join(",", g.Tools), g.Sha256 }).ToArray()),
                ToolSetHashJsonContext.Default.ToolSetBasisWithServices);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}

/// <summary>The hashed JSON without reflection (ADR 0060), byte for byte as the anonymous types before it: camelCase names, in declaration order.</summary>
[JsonSerializable(typeof(ToolSetBasis))]
[JsonSerializable(typeof(ToolSetBasisWithServices))]
internal sealed partial class ToolSetHashJsonContext : JsonSerializerContext;

internal sealed record ToolSetBasis(
    [property: JsonPropertyName("opencodeVersion")] string OpencodeVersion,
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("rules")] string[][] Rules);

internal sealed record ToolSetBasisWithServices(
    [property: JsonPropertyName("opencodeVersion")] string OpencodeVersion,
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("rules")] string[][] Rules,
    [property: JsonPropertyName("services")] string[][] Services);
