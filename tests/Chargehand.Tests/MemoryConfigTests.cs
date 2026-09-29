using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Memory;

namespace Chargehand.Tests;

/// <summary>Goal 0.6: memory is an ordered list of providers, each a server plus a declarative tool mapping.</summary>
public class MemoryConfigTests
{
    internal const string Hindsight = """
        {"name":"hindsight","server":"gw","namespace":"chargehand","tools":{
          "recall":{"tool":"recall","arguments":{"query":"{query}","bank_id":"{namespace}","budget":"low","max_tokens":1024}},
          "retain":{"tool":"retain","arguments":{"content":"{text}","context":"{context}","document_id":"{document_id}","timestamp":"{timestamp}","tags":"{tags}","bank_id":"{namespace}"}},
          "invalidate":{"tool":"invalidate_memory","arguments":{"memory_id":"{id}","reason":"{reason}","bank_id":"{namespace}"}}},
          "retain":true}
        """;

    private const string Gateway = """{"gw":{"url":"https://mcp.example.internal/mcp"}}""";

    private static Profile Load(string memory, string servers = Gateway)
    {
        using var dir = new TempDir();
        return Profile.Load(dir.Write("p.json", $$"""{"schema":"profile/v1","mcp_servers":{{servers}},"memory":{{memory}}}"""));
    }

    [Fact]
    public void A_memory_list_loads_with_its_mapping()
    {
        var provider = Load($"[{Hindsight}]").Memory!.Single();

        Assert.Equal(("hindsight", "gw", "chargehand", true), (provider.Name, provider.Server, provider.Namespace, provider.Retain));
        Assert.Equal("recall", provider.Tools.Recall.Tool);
        Assert.Equal("low", provider.Tools.Recall.Arguments["budget"].GetString());
        Assert.Equal(1024, provider.Tools.Recall.Arguments["max_tokens"].GetInt32());
        Assert.Equal("invalidate_memory", provider.Tools.Invalidate!.Tool);
        Assert.Null(provider.MaxFacts);
    }

    [Fact]
    public void A_results_mapping_names_the_array_and_the_fields()
    {
        var entry = """{"name":"notes","server":"gw","namespace":"n","tools":{"recall":{"tool":"search_notes","arguments":{"q":"{query}"},"results":{"path":"notes","id":"key","text":"body"}}}}""";

        var results = Load($"[{entry}]").Memory!.Single().Tools.Recall.Results!;

        Assert.Equal(("notes", "key", "json"), (results.Path, results.Id, results.Format));
        Assert.Equal(["body"], results.Text);
    }

    /// <summary>Chronicle's MCP server (github.com/Egoushka/chronicle): legacy SSE, recall only, no banks.</summary>
    internal const string Chronicle = """
        {"name":"chronicle","server":"chronicle","tools":{"recall":{"tool":"recall","arguments":{"query":"{query}","limit":"{max_facts}"},
          "results":{"path":"results","id":"segment_id","text":["{date}: {summary}","{date}: {text}"]}}}}
        """;

    private const string ChronicleServer = """{"chronicle":{"url":"http://localhost:8031/sse","transport":"sse"}}""";

    [Fact]
    public void A_recall_only_provider_loads_without_a_namespace_and_reads_a_list_of_text_templates()
    {
        var provider = Load($"[{Chronicle}]", ChronicleServer).Memory!.Single();

        Assert.Null(provider.Namespace);
        Assert.Equal("chronicle", provider.EffectiveNamespace);
        Assert.Null(provider.Tools.Retain);
        Assert.False(provider.Retain);
        Assert.Equal("{max_facts}", provider.Tools.Recall.Arguments["limit"].GetString());
        Assert.Equal(["{date}: {summary}", "{date}: {text}"], provider.Tools.Recall.Results!.Text);
    }

    [Fact]
    public void The_object_form_of_memory_fails_with_the_migration()
    {
        using var dir = new TempDir();
        var path = dir.Write("p.json", """{"schema":"profile/v1","memory":{"backend":"hindsight","url":"http://memory.example.internal:8888","namespace":"ns"}}""");

        var e = Assert.Throws<ChargehandException>(() => Profile.Load(path));

        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Contains("memory is a list now", e.Message, StringComparison.Ordinal);
        Assert.Contains("mcp_servers", e.Action, StringComparison.Ordinal);
    }

    [Fact]
    public void The_example_profile_holds_no_object_form()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Repo.Path("profiles", "example.json")));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("memory").ValueKind);
    }

    [Theory]
    [InlineData("""[{"name":"a","server":"nope","namespace":"n","tools":{"recall":{"tool":"r","arguments":{}}}}]""", "nope")]
    [InlineData("""[{"name":"a","server":"gw","namespace":"n","tools":{"recall":{"tool":"r","arguments":{"q":"{queryy}"}}}}]""", "{queryy}")]
    [InlineData("""[{"name":"a","server":"gw","namespace":"n","tools":{"recall":{"tool":"r","arguments":{"q":"{text}"}}}}]""", "{text}")]
    [InlineData("""[{"name":"a","server":"gw","tools":{"recall":{"tool":"r","arguments":{}},"retain":{"tool":"w","arguments":{"n":"{max_facts}"}}}}]""", "{max_facts}")]
    [InlineData("""[{"name":"a","server":"gw","namespace":"n","retain":true,"tools":{"recall":{"tool":"r","arguments":{}}}}]""", "retain")]
    [InlineData("""[{"name":"a","server":"gw","namespace":"n","tools":{"recall":{"tool":"r","arguments":{},"results":{"format":"xml"}}}}]""", "format")]
    [InlineData("""[{"name":"Bad_Name","server":"gw","namespace":"n","tools":{"recall":{"tool":"r","arguments":{}}}}]""", "name")]
    [InlineData("""[{"name":"a","server":"gw","namespace":"n","tools":{"recall":{"tool":"r","arguments":{}}}},{"name":"a","server":"gw","namespace":"m","tools":{"recall":{"tool":"r","arguments":{}}}}]""", "duplicate")]
    public void An_invalid_memory_entry_fails_at_load_and_says_why(string memory, string fragment)
    {
        var e = Assert.Throws<ChargehandException>(() => Load(memory));

        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Contains(fragment, e.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(e.Action));
    }

    /// <summary>One memory entry named "a" on the gateway; <paramref name="tools"/> is the inside of its "tools" object.</summary>
    private static string Entry(string tools) => "[{\"name\":\"a\",\"server\":\"gw\",\"tools\":{" + tools + "}}]";

    [Theory]
    [InlineData("""[{"name":"a","server":"gw"}]""", "tools")]                                                                                      // no tools block at all
    [InlineData("""[{"name":"a","server":"gw","tools":{}}]""", "recall")]                                                                          // no recall tool
    [InlineData("""[{"name":"a","tools":{"recall":{"tool":"r","arguments":{}}}}]""", "server")]                                                    // no server
    [InlineData("""[{"name":"a","server":"gw","tools":{"recall":{"arguments":{}}}}]""", "recall.tool")]                                           // no tool name
    [InlineData("""[{"name":"a","server":"gw","tools":{"recall":{"tool":"r"}}}]""", "recall.arguments")]                                          // no arguments
    [InlineData("""[{"name":"a","server":"gw","namespace":"","tools":{"recall":{"tool":"r","arguments":{}}}}]""", "namespace")]                   // an empty namespace
    [InlineData("""[{"name":"a","server":"gw","max_facts":0,"tools":{"recall":{"tool":"r","arguments":{}}}}]""", "max_facts")]                    // a limit below 1
    [InlineData("""[{"name":"a","server":"gw","timeout_seconds":-1,"tools":{"recall":{"tool":"r","arguments":{}}}}]""", "timeout_seconds")]
    public void A_missing_or_empty_field_fails_at_load_and_says_which(string memory, string fragment)
    {
        var e = Assert.Throws<ChargehandException>(() => Load(memory));

        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Contains(fragment, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_placeholder_is_checked_against_the_tool_it_sits_in_and_the_message_names_the_tool()
    {
        var e = Assert.Throws<ChargehandException>(() => Load(Entry("""
            "recall":{"tool":"r","arguments":{}},
            "invalidate":{"tool":"i","arguments":{"n":"{text}"}}
            """)));

        Assert.Contains("{text}", e.Message, StringComparison.Ordinal);
        Assert.Contains("invalidate", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Placeholders_are_found_in_nested_objects_and_arrays_and_the_message_says_where()
    {
        var e = Assert.Throws<ChargehandException>(() =>
            Load(Entry("""
                "recall":{"tool":"r","arguments":{"filter":{"tags":["a","{tagg}"]}}}
                """)));

        Assert.Contains("{tagg}", e.Message, StringComparison.Ordinal);
        Assert.Contains("filter.tags[1]", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_fault_is_listed_not_just_the_first()
    {
        var memory = """[{"name":"a","server":"nope","retain":true,"tools":{"recall":{"tool":"r","arguments":{"q":"{text}"},"results":{"format":"xml"}}}}]""";

        var e = Assert.Throws<ChargehandException>(() => Load(memory));

        foreach (var fragment in new[] { "nope", "{text}", "retain", "format" })
            Assert.Contains(fragment, e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"q":"Bearer abc{def"}""")]      // a brace that never closes
    [InlineData("""{"q":"{Bearer sk-live-abc}"}""")] // braces around something that is no placeholder name
    [InlineData("""{"q":"x} sk-live-abc"}""")]       // a closing brace with no opening one
    public void A_malformed_brace_is_refused_without_quoting_the_argument(string arguments)
    {
        var e = Assert.Throws<ChargehandException>(() => Load(Entry("\"recall\":{\"tool\":\"r\",\"arguments\":" + arguments + "}")));

        Assert.Contains("'q'", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"text":"{date"}""", "results.text")]      // an unclosed template
    [InlineData("""{"text":[]}""", "results.text")]           // no entry to try
    [InlineData("""{"text":["body",""]}""", "results.text")]  // an empty entry
    [InlineData("""{"id":""}""", "results.id")]
    [InlineData("""{"path":"a..b"}""", "results.path")]       // an empty segment
    public void A_results_mapping_that_cannot_work_fails_at_load(string results, string fragment)
    {
        var e = Assert.Throws<ChargehandException>(() => Load(Entry("\"recall\":{\"tool\":\"r\",\"arguments\":{},\"results\":" + results + "}")));

        Assert.Contains(fragment, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_results_mapping_may_leave_every_field_out_and_a_field_name_may_hold_dashes_and_dots()
    {
        var entry = Entry("""
            "recall":{"tool":"r","arguments":{},"results":{"path":"data.items","text":["{created-at}: {body.text}","body"]}}
            """);

        var results = Load(entry).Memory!.Single().Tools.Recall.Results!;

        Assert.Equal(("data.items", "id", "json"), (results.Path, results.Id, results.Format));
        Assert.Equal(["{created-at}: {body.text}", "body"], results.Text);
    }

    [Fact]
    public void Without_a_results_block_recall_reads_the_results_array_and_a_text_field()
    {
        var call = Load($"[{Hindsight}]").Memory!.Single().Tools.Recall;

        Assert.Null(call.Results);
        Assert.Equal(("results", "id", "json"), (call.EffectiveResults.Path, call.EffectiveResults.Id, call.EffectiveResults.Format));
        Assert.Equal(["text"], call.EffectiveResults.EffectiveText);
    }

    [Fact]
    public void A_results_block_without_a_path_reads_the_root_and_without_text_reads_the_text_field()
    {
        var entry = Entry("""
            "recall":{"tool":"r","arguments":{},"results":{"id":"key"}}
            """);

        var results = Load(entry).Memory!.Single().Tools.Recall.EffectiveResults;

        Assert.Null(results.Path);
        Assert.Equal("key", results.Id);
        Assert.Equal(["text"], results.EffectiveText);
    }

    [Fact]
    public void Limits_default_to_the_stacks_and_an_entry_overrides_them()
    {
        var notes = """{"name":"notes","server":"gw","max_facts":3,"max_chars":900,"max_fact_chars":200,"timeout_seconds":4,"retain_tags":["team"],"tools":{"recall":{"tool":"r","arguments":{}}}}""";
        var providers = Load($"[{Chronicle.Replace("\"server\":\"chronicle\"", "\"server\":\"gw\"", StringComparison.Ordinal)},{notes}]").Memory!;

        Assert.Equal(new MemoryLimits(), providers[0].Limits);
        Assert.Equal(new MemoryLimits(3, 900, TimeSpan.FromSeconds(4), 200), providers[1].Limits);
        Assert.Equal(["chargehand"], providers[0].EffectiveRetainTags);
        Assert.Equal(["team"], providers[1].EffectiveRetainTags);
    }

    [Fact]
    public void The_two_mappings_of_the_spec_pass_validation_as_written()
    {
        var servers = new Dictionary<string, McpServerSettings> { ["gw"] = new(Url: "https://mcp.example.internal/mcp"), ["chronicle"] = new(Url: "http://localhost:8031/sse", Transport: "sse") };
        var hindsight = JsonSerializer.Deserialize<MemoryProviderSettings>(Hindsight, Profile.Json)!;
        var chronicle = JsonSerializer.Deserialize<MemoryProviderSettings>(Chronicle, Profile.Json)!;

        Assert.Empty(MemoryMapping.Validate(hindsight, servers));
        Assert.Empty(MemoryMapping.Validate(chronicle, servers));
        Assert.Empty(MemoryMapping.Validate([hindsight, chronicle], servers));
    }

    [Fact]
    public void The_same_name_twice_is_reported_by_the_list_check_and_not_by_the_single_check()
    {
        var servers = new Dictionary<string, McpServerSettings> { ["gw"] = new(Url: "https://mcp.example.internal/mcp") };
        var hindsight = JsonSerializer.Deserialize<MemoryProviderSettings>(Hindsight, Profile.Json)!;

        Assert.Empty(MemoryMapping.Validate(hindsight, servers));
        Assert.Contains("duplicate", Assert.Single(MemoryMapping.Validate([hindsight, hindsight], servers)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"not a list or object\"")]
    [InlineData("42")]
    [InlineData("[null]")]
    [InlineData("[\"hindsight\"]")]
    public void A_memory_value_that_is_is_not_a_list_of_entries_is_a_json_error(string memory)
    {
        Assert.ThrowsAny<JsonException>(() => Load(memory));
    }

    [Fact]
    public void The_example_profile_loads_with_both_mappings()
    {
        var memory = Profile.Load(Repo.Path("profiles", "example.json")).Memory!;

        Assert.Equal(["hindsight", "chronicle"], memory.Select(p => p.Name));
        Assert.Equal([false, false], memory.Select(p => p.Retain));
    }
}
