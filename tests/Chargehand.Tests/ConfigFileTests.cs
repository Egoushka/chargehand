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

    /// <summary>Spec decision 2: naked by default. Services belong to a user's own preset; the guide shows one.</summary>
    [Theory]
    [MemberData(nameof(Presets))]
    public void Shipped_presets_list_no_services(string file)
    {
        var preset = Chargehand.Config.Preset.Load(Repo.Path("presets"), file[..^5]);
        Assert.All(preset.NodeKinds.Values, k => Assert.True(k.Services is null or { Count: 0 }));
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

    // JsonSchema.FromText registers the schema globally by its base URI; loading the same file twice throws.
    private static readonly JsonSchema ProfileSchema = JsonSchema.FromText(File.ReadAllText(Repo.Path("profiles", "profile.schema.json")));

    [Fact]
    public void Example_profile_validates()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Repo.Path("profiles", "example.json")));
        Assert.True(ProfileSchema.Evaluate(doc.RootElement).IsValid);
    }

    /// <summary>ADR 0026: every field but "schema" is now optional.</summary>
    [Fact]
    public void A_minimal_profile_with_only_the_schema_field_validates()
    {
        using var doc = JsonDocument.Parse("""{"schema":"profile/v1"}""");
        Assert.True(ProfileSchema.Evaluate(doc.RootElement).IsValid);
    }

    [Theory]
    [InlineData("""{"schema":"profile/v1","mcp_servers":{"gw":{"url":"https://a.example.internal/mcp","headers":{"Authorization":"Bearer {secret:t}"}}}}""", true)]
    [InlineData("""{"schema":"profile/v1","mcp_servers":{"gw":{"command":["npx","-y","x"],"env":{"T":"{secret:t}"}}}}""", true)]
    [InlineData("""{"schema":"profile/v1","mcp_servers":{"gw":{"url":"http://localhost:8080/sse","transport":"sse"}}}""", true)]
    [InlineData("""{"schema":"profile/v1","mcp_servers":{"gw":{"url":"https://a.example.internal/mcp","command":["x"]}}}""", false)]      // both transports
    [InlineData("""{"schema":"profile/v1","mcp_servers":{"gw":{}}}""", false)]                                                             // neither
    [InlineData("""{"schema":"profile/v1","mcp_servers":{"gw":{"url":"https://a.example.internal/mcp?key={secret:k}"}}}""", false)]      // a secret in a URL
    [InlineData("""{"schema":"profile/v1","mcp_servers":{"gw":{"command":["x","--key={secret:k}"]}}}""", false)]                        // a secret in argv
    [InlineData("""{"schema":"profile/v1","mcp_servers":{"Bad_Name":{"url":"https://a.example.internal/mcp"}}}""", false)]               // name pattern
    [InlineData("""{"schema":"profile/v1","mcp_servers":{"gw":{"command":["x"],"headers":{"a":"b"}}}}""", false)]                        // headers on stdio
    [InlineData("""{"schema":"profile/v1","mcp_servers":{"gw":{"url":"https://a.example.internal/mcp","env":{"a":"b"}}}}""", false)]    // env on a url server
    [InlineData("""{"schema":"profile/v1","mcp_servers":{"gw":{"command":["x"],"transport":"sse"}}}""", false)]                          // transport on stdio
    [InlineData("""{"schema":"profile/v1","mcp_servers":{"gw":{"url":"https://a.example.internal/mcp","transport":"pigeon"}}}""", false)] // unknown transport
    public void The_schema_checks_mcp_servers(string profile, bool valid)
    {
        using var doc = JsonDocument.Parse(profile);
        Assert.Equal(valid, ProfileSchema.Evaluate(doc.RootElement).IsValid);
    }

    [Theory]
    [InlineData("""{"schema":"profile/v1","prompt_enhancer":{"server":"w"}}""", true)]
    [InlineData("""{"schema":"profile/v1","prompt_enhancer":{"server":"w","deadline_ms":800}}""", true)]
    [InlineData("""{"schema":"profile/v1","prompt_enhancer":{}}""", false)]                                  // no server
    [InlineData("""{"schema":"profile/v1","prompt_enhancer":{"server":"w","deadline_ms":10}}""", false)]     // below the contract's minimum
    [InlineData("""{"schema":"profile/v1","prompt_enhancer":{"server":"w","tools":{}}}""", false)]           // no tool mapping: the contract is fixed
    public void The_schema_checks_prompt_enhancer(string profile, bool valid)
    {
        using var doc = JsonDocument.Parse(profile);
        Assert.Equal(valid, ProfileSchema.Evaluate(doc.RootElement).IsValid);
    }

    [Fact]
    public void A_recall_only_provider_with_no_namespace_passes_the_schema()
    {
        using var doc = JsonDocument.Parse("""{"schema":"profile/v1","memory":[{"name":"chronicle","server":"chronicle","tools":{"recall":{"tool":"recall","arguments":{"query":"{query}","limit":"{max_facts}"},"results":{"path":"results","id":"segment_id","text":["{date}: {summary}","{date}: {text}"]}}}}]}""");
        Assert.True(ProfileSchema.Evaluate(doc.RootElement).IsValid);
    }

    [Fact]
    public void A_memory_provider_without_a_recall_tool_fails_the_schema()
    {
        using var doc = JsonDocument.Parse("""{"schema":"profile/v1","memory":[{"name":"a","server":"gw","namespace":"n","tools":{}}]}""");
        Assert.False(ProfileSchema.Evaluate(doc.RootElement).IsValid);
    }

    [Theory]
    [InlineData("""{"schema":"profile/v1","memory":[]}""", true)]
    [InlineData("""{"schema":"profile/v1","memory":{"backend":"hindsight","url":"http://memory.example.internal:8888","namespace":"ns"}}""", false)]  // the object form, removed
    [InlineData("""{"schema":"profile/v1","memory":[{"name":"a","server":"gw","namespace":"n","tools":{"recall":{"tool":"r","arguments":{},"results":{"text":"body"}}}}]}""", true)]
    [InlineData("""{"schema":"profile/v1","memory":[{"name":"a","server":"gw","max_facts":3,"max_chars":900,"max_fact_chars":200,"timeout_seconds":4,"retain":true,"retain_tags":["t"],"tools":{"recall":{"tool":"r","arguments":{}},"retain":{"tool":"w","arguments":{}}}}]}""", true)]
    [InlineData("""{"schema":"profile/v1","memory":[{"name":"a","server":"gw","retain":true,"tools":{"recall":{"tool":"r","arguments":{}}}}]}""", false)]           // retain with no retain tool
    [InlineData("""{"schema":"profile/v1","memory":[{"name":"Bad_Name","server":"gw","tools":{"recall":{"tool":"r","arguments":{}}}}]}""", false)]                   // name pattern
    [InlineData("""{"schema":"profile/v1","memory":[{"name":"a","tools":{"recall":{"tool":"r","arguments":{}}}}]}""", false)]                                       // no server
    [InlineData("""{"schema":"profile/v1","memory":[{"name":"a","server":"gw","tools":{"recall":{"tool":"r"}}}]}""", false)]                                        // no arguments
    [InlineData("""{"schema":"profile/v1","memory":[{"name":"a","server":"gw","tools":{"recall":{"tool":"r","arguments":{},"results":{"format":"xml"}}}}]}""", false)] // unknown format
    [InlineData("""{"schema":"profile/v1","memory":[{"name":"a","server":"gw","tools":{"recall":{"tool":"r","arguments":{},"results":{"text":[]}}}}]}""", false)]      // no text entry
    [InlineData("""{"schema":"profile/v1","memory":[{"name":"a","server":"gw","max_facts":0,"tools":{"recall":{"tool":"r","arguments":{}}}}]}""", false)]              // limit below 1
    [InlineData("""{"schema":"profile/v1","memory":[{"name":"a","server":"gw","namespace":"","tools":{"recall":{"tool":"r","arguments":{}}}}]}""", false)]            // empty namespace
    [InlineData("""{"schema":"profile/v1","memory":"hindsight"}""", false)]
    public void The_schema_checks_memory(string profile, bool valid)
    {
        using var doc = JsonDocument.Parse(profile);
        Assert.Equal(valid, ProfileSchema.Evaluate(doc.RootElement).IsValid);
    }
}
