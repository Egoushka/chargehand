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

    /// <summary>ADR 0006: every preset denies secret files after its broad allow.</summary>
    [Theory]
    [MemberData(nameof(Presets))]
    public void Shipped_presets_deny_secrets(string file)
    {
        var preset = Chargehand.Config.Preset.Load(Repo.Path("presets"), file[..^5]);
        foreach (var kind in preset.NodeKinds.Values)
        {
            var rules = kind.Permissions.Select(p => $"{p.Action} {p.Resource} {p.Effect}").ToList();
            // A preset without a broad allow (draft: every tool denied) must allow nothing at all.
            if (!rules.Contains("* * allow"))
            {
                Assert.DoesNotContain(kind.Permissions, p => p.Effect == "allow");
                continue;
            }
            foreach (var required in new[] { "read *.env deny", "read *.env.* deny" })
                Assert.True(rules.LastIndexOf(required) > rules.IndexOf("* * allow"), $"{file}: '{required}' missing or before the broad allow");
        }
    }

    /// <summary>
    /// ADR 0006: no preset gives a worker the shell tool. OpenCode 2.0.16 drops a tool from the catalog when the last
    /// rule whose action matches it is "* deny"; any shell allow after that would match quoting-dependent source text.
    /// </summary>
    [Theory]
    [MemberData(nameof(Presets))]
    public void Shipped_presets_remove_the_shell_tool(string file)
    {
        var preset = Chargehand.Config.Preset.Load(Repo.Path("presets"), file[..^5]);
        foreach (var kind in preset.NodeKinds.Values)
        {
            var last = kind.Permissions.Last(p => p.Action is "shell" or "*");
            Assert.True(last is { Resource: "*", Effect: "deny" }, $"{file}: last shell rule is '{last.Action} {last.Resource} {last.Effect}'");
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
