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
        var yaml = new DeserializerBuilder().WithAttemptingUnquotedStringTypeDeserialization().Build()
            .Deserialize(new StringReader(File.ReadAllText(Path.Combine(directory, name + ".yaml"))));
        var json = JsonSerializer.SerializeToElement(yaml);
        var errors = ContractSchemas.Validate(ContractSchemas.Preset, json);
        if (errors.Count > 0)
            throw new InvalidDataException($"preset '{name}': {string.Join("; ", errors)}");
        return json.Deserialize<Preset>(Profile.Json)!;
    }
}

/// <param name="Checkout">False: the node needs no repository; it runs in an empty directory under worker_root and intake
/// does not ask for context.repository (the draft preset, ADR 0018).</param>
public sealed record NodeKind(string Model, string OpencodeAgent, IReadOnlyList<RuleEntry> Permissions, NodeBudget Budget, CompactionSettings? Compaction = null,
    bool Checkout = true)
{
    public IReadOnlyList<PermissionRule> Rules =>
        Permissions.Select(p => new PermissionRule(p.Action, p.Resource, Enum.Parse<PermissionEffect>(p.Effect, ignoreCase: true))).ToList();
}

public sealed record RuleEntry(string Action, string Resource, string Effect);

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
