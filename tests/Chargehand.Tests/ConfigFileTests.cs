using System.Text.Json;
using Chargehand.Contracts;
using Json.Schema;
using YamlDotNet.Serialization;

namespace Chargehand.Tests;

public class ConfigFileTests
{
    public static TheoryData<string> Presets() => new(Directory.GetFiles(Repo.Path("presets"), "*.yaml").Select(System.IO.Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(Presets))]
    public void Shipped_presets_validate(string file)
    {
        var yaml = new DeserializerBuilder().WithAttemptingUnquotedStringTypeDeserialization().Build()
            .Deserialize(new StringReader(File.ReadAllText(Repo.Path("presets", file))));
        var json = JsonSerializer.SerializeToElement(yaml);
        var errors = ContractSchemas.Validate(ContractSchemas.Preset, json);
        Assert.True(errors.Count == 0, string.Join("; ", errors));
        Assert.DoesNotContain("\"always\"", json.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>ADR 0006: every preset denies secret files and shell reads that bypass .gitignore, after its broad allow.</summary>
    [Theory]
    [MemberData(nameof(Presets))]
    public void Shipped_presets_deny_secrets(string file)
    {
        var preset = Chargehand.Config.Preset.Load(Repo.Path("presets"), file[..^5]);
        foreach (var kind in preset.NodeKinds.Values)
        {
            var rules = kind.Permissions.Select(p => $"{p.Action} {p.Resource} {p.Effect}").ToList();
            foreach (var required in new[] { "read *.env deny", "read *.env.* deny", "shell rg *--no-ignore* deny", "shell rg * -u* deny" })
                Assert.True(rules.LastIndexOf(required) > rules.IndexOf("* * allow"), $"{file}: '{required}' missing or before the broad allow");
        }
    }

    [Fact]
    public void Example_profile_validates()
    {
        var schema = JsonSchema.FromText(File.ReadAllText(Repo.Path("profiles", "profile.schema.json")));
        using var doc = JsonDocument.Parse(File.ReadAllText(Repo.Path("profiles", "example.json")));
        Assert.True(schema.Evaluate(doc.RootElement).IsValid);
    }
}
