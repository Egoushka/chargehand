using System.Text.Json;
using Chargehand.Mcp;

namespace Chargehand.Tests;

public class ToolArgumentsTests
{
    private static IReadOnlyDictionary<string, JsonElement> Template(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static readonly string[] Tags = ["chargehand", "x"];

    private static readonly string[] AB = ["a", "b"];

    [Fact]
    public void A_whole_placeholder_keeps_the_values_type()
    {
        var args = ToolArguments.Expand(Template("""{"tags":"{tags}","n":"{count}","max_tokens":1024,"flag":true}"""),
            new Dictionary<string, object?> { ["tags"] = Tags, ["count"] = 3 });

        Assert.Equal(Tags, args["tags"]);
        Assert.Equal(3, args["n"]);
        Assert.Equal(1024, ((JsonElement)args["max_tokens"]!).GetInt32());
        Assert.True(((JsonElement)args["flag"]!).GetBoolean());
    }

    [Fact]
    public void A_placeholder_inside_text_is_replaced_as_text()
    {
        var args = ToolArguments.Expand(Template("""{"q":"repo:{namespace} {query}"}"""),
            new Dictionary<string, object?> { ["namespace"] = "chargehand", ["query"] = "deploys" });

        Assert.Equal("repo:chargehand deploys", args["q"]);
    }

    [Fact]
    public void A_number_or_a_list_inside_text_is_written_the_way_json_writes_it()
    {
        var args = ToolArguments.Expand(Template("""{"q":"{count} of {tags}"}"""),
            new Dictionary<string, object?> { ["count"] = 1234.5, ["tags"] = AB });

        Assert.Equal("""1234.5 of ["a","b"]""", args["q"]);
    }

    [Fact]
    public void A_null_value_omits_the_argument()
    {
        var args = ToolArguments.Expand(Template("""{"timestamp":"{timestamp}","content":"{text}"}"""),
            new Dictionary<string, object?> { ["timestamp"] = null, ["text"] = "fact" });

        Assert.False(args.ContainsKey("timestamp"));
        Assert.Equal("fact", args["content"]);
    }

    [Fact]
    public void A_null_value_inside_text_reads_as_nothing()
    {
        var args = ToolArguments.Expand(Template("""{"q":"[{context}] {text}"}"""),
            new Dictionary<string, object?> { ["context"] = null, ["text"] = "fact" });

        Assert.Equal("[] fact", args["q"]);
    }

    [Fact]
    public void Nested_objects_and_arrays_are_expanded()
    {
        var args = ToolArguments.Expand(Template("""{"filter":{"tags":["a","{tag}"]}}"""), new Dictionary<string, object?> { ["tag"] = "b" });

        Assert.Equal("""{"tags":["a","b"]}""", JsonSerializer.Serialize(args["filter"]));
    }

    [Fact]
    public void A_null_value_nested_in_an_object_or_an_array_is_dropped_too()
    {
        var args = ToolArguments.Expand(Template("""{"filter":{"ctx":"{context}","tags":["a","{context}"],"n":null}}"""),
            new Dictionary<string, object?> { ["context"] = null });

        Assert.Equal("""{"tags":["a"],"n":null}""", JsonSerializer.Serialize(args["filter"]));
    }

    [Fact]
    public void A_placeholder_followed_by_a_newline_is_text_not_a_whole_placeholder()
    {
        var args = ToolArguments.Expand(Template("""{"a":"{count}\n"}"""), new Dictionary<string, object?> { ["count"] = 3 });

        Assert.Equal("3\n", args["a"]);
    }

    [Fact]
    public void A_placeholder_the_call_does_not_offer_is_a_failure_that_names_no_value()
    {
        var e = Assert.Throws<McpMemoryException>(() => ToolArguments.Expand(Template("""{"a":"{nope}"}"""), new Dictionary<string, object?> { ["query"] = "s3cret" }));

        Assert.DoesNotContain("s3cret", e.Message, StringComparison.Ordinal);
    }
}
