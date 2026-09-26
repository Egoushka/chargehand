using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Runtime;
using YamlDotNet.Serialization;

namespace Chargehand.Config;

/// <summary>preset/v1, loaded from presets/&lt;name&gt;.yaml and validated against the schema.</summary>
public sealed record Preset(string Name, string Version, IReadOnlyList<string> AllowedActions, IReadOnlyDictionary<string, NodeKind> NodeKinds)
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

public sealed record NodeKind(string Model, string OpencodeAgent, IReadOnlyList<RuleEntry> Permissions, NodeBudget Budget)
{
    public IReadOnlyList<PermissionRule> Rules =>
        Permissions.Select(p => new PermissionRule(p.Action, p.Resource, Enum.Parse<PermissionEffect>(p.Effect, ignoreCase: true))).ToList();
}

public sealed record RuleEntry(string Action, string Resource, string Effect);

public sealed record NodeBudget(long MaxInputTokens, decimal MaxUsd);
