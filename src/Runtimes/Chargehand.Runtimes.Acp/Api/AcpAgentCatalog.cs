using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Runtimes.Acp.Api;

/// <summary>
/// The agents a profile may name, pinned in <c>acp-agents.json</c> (ADR 0086, decision 8). The file is refreshed from the ACP registry by
/// <c>scripts/refresh-acp-agents.py</c> and reviewed like code; a run never fetches a list.
/// </summary>
public sealed partial class AcpAgentCatalog
{
    public const string FileName = "acp-agents.json";

    public const string Schema = "acp-agents/v1";

    private readonly IReadOnlyDictionary<string, AcpAgentSpec> _byId;

    private AcpAgentCatalog(IReadOnlyList<AcpAgentSpec> agents)
    {
        Agents = agents;
        _byId = agents.ToDictionary(a => a.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<AcpAgentSpec> Agents { get; }

    public static AcpAgentCatalog Load(string path)
    {
        if (!File.Exists(path))
            throw Bad($"{path} does not exist", "Add the pinned list of ACP agents there, or point acp.agents_file at it.");
        return Parse(File.ReadAllText(path), path);
    }

    public static AcpAgentCatalog Parse(string json, string origin)
    {
        AcpAgentFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, AcpAgentsJsonContext.Default.AcpAgentFile);
        }
        catch (JsonException e)
        {
            throw Bad($"{origin} is not valid JSON: {e.Message}", $"Fix {origin}.");
        }

        if (file?.Schema != Schema)
            throw Bad($"{origin}: schema must be {Schema}", $"Set \"schema\" to \"{Schema}\" in {origin}.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var agent in file.Agents ?? [])
        {
            Validate(agent, origin);
            if (!seen.Add(agent.Id))
                throw Bad($"{origin}: agent '{agent.Id}' is listed twice", "Keep one entry per id.");
        }

        return new AcpAgentCatalog([.. file.Agents ?? []]);
    }

    public bool Contains(string id) => _byId.ContainsKey(id);

    /// <summary>The pinned agent, or an error naming the ones that are.</summary>
    public AcpAgentSpec Get(string id) =>
        _byId.TryGetValue(id, out var agent)
            ? agent
            : throw new ChargehandException(ErrorCode.InvalidRequest, $"unknown ACP agent '{id}'; pinned: {string.Join(", ", _byId.Keys.Order(StringComparer.Ordinal))}",
                $"Name one of them as runtime acp:<id>, or add the agent to {FileName} after reviewing it.");

    private static void Validate(AcpAgentSpec agent, string origin)
    {
        foreach (var (field, value) in new[] { ("id", agent.Id), ("command", agent.Command), ("version", agent.Version), ("api_key_env", agent.ApiKeyEnv) })
            if (string.IsNullOrWhiteSpace(value))
                throw Bad($"{origin}: agent '{agent.Id}' has no {field}", $"Every agent needs an id, command, version and api_key_env (a run never uses a login).");
        if (agent.Sha256 is { } sha && !Sha256Hex().IsMatch(sha))
            throw Bad($"{origin}: agent '{agent.Id}' has a sha256 that is not 64 hex digits", "Copy the hash from the registry entry, or remove it.");
    }

    private static ChargehandException Bad(string message, string action) => new(ErrorCode.InvalidRequest, message, action);

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Hex();
}

internal sealed record AcpAgentFile(string? Schema, IReadOnlyList<AcpAgentSpec>? Agents);
