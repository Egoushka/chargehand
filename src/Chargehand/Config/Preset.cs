using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Runtime;
using YamlDotNet.Serialization;

namespace Chargehand.Config;

/// <summary>preset/v1, loaded from presets/&lt;name&gt;.yaml and validated against the schema.</summary>
public sealed record Preset(
    string Name,
    string Version,
    IReadOnlyList<string> AllowedActions,
    IReadOnlyDictionary<string, NodeKind> NodeKinds,
    ApprovalSettings? Approval = null)
{
    public static Preset Load(string directory, string name)
    {
        var path = Path.Combine(directory, name + ".yaml");
        if (!File.Exists(path))
            throw new ChargehandException(ErrorCode.InvalidRequest, $"unknown preset '{name}'",
                $"Use one of {string.Join(", ", (Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.yaml") : []).Select(Path.GetFileNameWithoutExtension).Order())}, or add {name}.yaml to the presets directory.");
        var yaml = new DeserializerBuilder().WithAttemptingUnquotedStringTypeDeserialization().Build()
            .Deserialize(new StringReader(File.ReadAllText(path)));
        var json = JsonSerializer.SerializeToElement(yaml);
        var errors = ContractSchemas.Validate(ContractSchemas.Preset, json);
        if (errors.Count > 0)
            throw new InvalidDataException($"preset '{name}': {string.Join("; ", errors)}");
        return json.Deserialize<Preset>(Profile.Json)!;
    }
}

/// <param name="Checkout">False: the node needs no repository; it runs in an empty directory under worker_root and intake
/// does not ask for context.repository (the draft preset, ADR 0018).</param>
/// <param name="Writes">True: the node edits files in a per-run clone and returns a verified branch (ADR 0035).</param>
/// <param name="Services">MCP servers of the profile whose named tools the node's workers may call (ADR 0034); null: none.</param>
public sealed record NodeKind(string Model, string OpencodeAgent, IReadOnlyList<RuleEntry> Permissions, NodeBudget Budget, CompactionSettings? Compaction = null,
    bool Checkout = true, IReadOnlyList<ServiceUse>? Services = null, bool Writes = false, VerifySettings? Verify = null)
{
    public IReadOnlyList<PermissionRule> Rules =>
        Permissions.Select(p => new PermissionRule(p.Action, p.Resource, Enum.Parse<PermissionEffect>(p.Effect, ignoreCase: true))).ToList();
}

/// <summary>A writing node's verification (ADR 0035): the test command's timeout and how many failed runs go back to the worker.</summary>
public sealed record VerifySettings(int TimeoutSeconds = 600, int MaxFixRounds = 2);

public sealed record RuleEntry(string Action, string Resource, string Effect);

/// <summary>A server of the profile's <c>mcp_servers</c> and the tools of it a node may use: exact names, or globs where <c>*</c> is
/// any run of characters (never the server's whole set, ADR 0034).</summary>
public sealed record ServiceUse(string Server, IReadOnlyList<string> Tools)
{
    public bool Equals(ServiceUse? other) => other is not null && Server == other.Server && Tools.SequenceEqual(other.Tools);

    public override int GetHashCode() => HashCode.Combine(Server, Tools.Count);
}

/// <param name="MaxInputTokens">Prompt tokens (input + cache read + cache write) summed over the node's calls; the node is interrupted above it.</param>
public sealed record NodeBudget(long MaxInputTokens, decimal MaxUsd);

/// <param name="TriggerTokens">The orchestrator compacts the session (steered, mid-turn) once a call's context exceeds this.
/// OpenCode 2.0.16 takes no compaction settings per session; auto, keep_tokens and buffer describe the server config.</param>
public sealed record CompactionSettings(bool? Auto = null, long? KeepTokens = null, long? Buffer = null, long? TriggerTokens = null);

/// <summary>Runs above either threshold stop with needs_input unless the request says approved (ADR 0006, strict).</summary>
public sealed record ApprovalSettings(string? AskAboveRisk = null, decimal? AskAboveUsd = null)
{
    public bool Requires(TaskSpec spec) =>
        (AskAboveRisk is { } risk && spec.Risk > Enum.Parse<Risk>(risk, ignoreCase: true)) || (AskAboveUsd is { } usd && spec.Estimate.UsdHigh > usd);
}
