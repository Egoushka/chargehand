using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chargehand.Runtime;

namespace Chargehand.Prompts;

/// <summary>Stays in the old project: it hashes <see cref="PermissionRule"/> and <see cref="ServiceGrant"/>, which still live in <c>Chargehand.Runtime</c>.</summary>
public static class ToolSetHash
{
    /// <summary>Proxy for OpenCode's tool catalog, which is a function of its version, the agent and the ruleset, and of the
    /// MCP tools granted (server, tool names, description hash); the same as without grants when there are none.</summary>
    public static string ToolsSha256(string opencodeVersion, string agent, IReadOnlyList<PermissionRule> rules, IReadOnlyList<ServiceGrant>? grants = null)
    {
        var ruleList = rules.Select(r => new[] { r.Action, r.Resource, r.Effect.ToString() });
        var json = grants is not { Count: > 0 }
            ? JsonSerializer.Serialize(new { opencodeVersion, agent, rules = ruleList })
            : JsonSerializer.Serialize(new { opencodeVersion, agent, rules = ruleList, services = grants.Select(g => new[] { g.Server, string.Join(",", g.Tools), g.Sha256 }) });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
