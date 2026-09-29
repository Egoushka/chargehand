using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Mcp;
using Chargehand.Memory;
using ModelContextProtocol.Protocol;

namespace Chargehand.Tests;

/// <summary>How a recall tool's answer becomes facts (ResultMapping): the path, the id, the ordered text entries.</summary>
public class RecallResultsTests
{
    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };

    private static CallToolResult Both(string structured, string text) => new() { Content = [new TextContentBlock { Text = text }], StructuredContent = Json(structured) };

    private static ResultMapping Mapping(string json) => JsonSerializer.Deserialize<ResultMapping>(json, Profile.Json)!;

    private static string Sha12(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    private static IReadOnlyList<RecalledMemory> Read(string answer, string mapping) => RecallResults.Read(Text(answer), Mapping(mapping));

    [Fact]
    public void No_mapping_reads_the_shape_hindsight_answers()
    {
        var facts = RecallResults.Read(Text("""{"results":[{"id":"f1","text":"one","extra":1},{"id":"f2","text":"two"}]}"""), null);

        Assert.Equal([new RecalledMemory("f1", "one"), new RecalledMemory("f2", "two")], facts);
    }

    [Fact]
    public void A_dotted_path_walks_objects_and_a_missing_path_reads_the_root()
    {
        Assert.Equal([new RecalledMemory("a", "deep")], Read("""{"data":{"items":[{"id":"a","text":"deep"}]}}""", """{"path":"data.items"}"""));
        Assert.Equal([new RecalledMemory("a", "root")], Read("""[{"id":"a","text":"root"}]""", "{}"));
        Assert.Equal([new RecalledMemory("a", "root")], Read("""[{"id":"a","text":"root"}]""", """{"path":""}"""));
    }

    [Theory]
    [InlineData("""{"other":[]}""")]
    [InlineData("""{"results":{"a":1}}""")]
    [InlineData("""{"results":"x"}""")]
    [InlineData("""[]""")]
    [InlineData("not json")]
    [InlineData("")]
    public void An_answer_the_path_cannot_be_followed_in_is_a_failure(string answer)
    {
        Assert.Throws<McpMemoryException>(() => Read(answer, """{"path":"results"}"""));
    }

    [Fact]
    public void An_error_result_is_a_failure_and_its_text_is_scrubbed()
    {
        var e = Assert.Throws<McpMemoryException>(() =>
            RecallResults.Read(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "boom, token: s3cret" }] }, null));

        Assert.Contains("boom", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_answer_with_neither_text_nor_structured_content_is_a_failure()
    {
        Assert.Throws<McpMemoryException>(() => RecallResults.Read(new CallToolResult { Content = [] }, null));
    }

    [Fact]
    public void An_empty_array_is_no_facts_not_a_failure()
    {
        Assert.Empty(Read("""{"results":[]}""", """{"path":"results"}"""));
    }

    [Fact]
    public void Structured_content_is_read_before_the_text_block()
    {
        var facts = RecallResults.Read(Both("""{"results":[{"id":"s","text":"structured"}]}""", """{"results":[{"id":"t","text":"text"}]}"""), null);

        Assert.Equal([new RecalledMemory("s", "structured")], facts);
    }

    /// <summary>FastMCP answers a tool that returns str as {"result": "<the str>"} next to the text block, so a path that
    /// finds nothing in the structured content is read from the text block.</summary>
    [Fact]
    public void A_path_that_finds_nothing_in_the_structured_content_is_read_from_the_text_block()
    {
        var facts = RecallResults.Read(Both("""{"result":"{\"results\":[{\"id\":\"t\",\"text\":\"text\"}]}"}""", """{"results":[{"id":"t","text":"text"}]}"""), null);

        Assert.Equal([new RecalledMemory("t", "text")], facts);
    }

    [Fact]
    public void Structured_content_alone_is_enough_and_a_failing_one_fails()
    {
        var structured = new CallToolResult { StructuredContent = Json("""{"results":[{"id":"s","text":"only structured"}]}""") };
        Assert.Equal([new RecalledMemory("s", "only structured")], RecallResults.Read(structured, null));

        var wrong = new CallToolResult { StructuredContent = Json("""{"result":"x"}""") };
        Assert.Throws<McpMemoryException>(() => RecallResults.Read(wrong, null));
    }

    [Fact]
    public void The_id_is_the_named_property_a_number_as_written_or_a_hash_of_the_text()
    {
        var facts = Read("""{"n":[{"key":7,"body":"seven"},{"key":"k","body":"text key"},{"body":"none"},{"key":"","body":"empty"},{"key":null,"body":"null"},{"key":{"a":1},"body":"object"}]}""",
            """{"path":"n","id":"key","text":"body"}""");

        Assert.Equal([new("7", "seven"), new("k", "text key"), new(Sha12("none"), "none"), new(Sha12("empty"), "empty"), new(Sha12("null"), "null"), new(Sha12("object"), "object")], facts);
    }

    [Fact]
    public void A_bare_text_entry_is_a_field_that_must_be_a_non_empty_string_or_a_number()
    {
        var facts = Read("""[{"text":"s"},{"text":""},{"text":12},{"text":null},{"text":true},{"text":["a"]},{"text":{"a":1}},{"other":"x"},"loose",3]""", """{"path":""}""");

        Assert.Equal(["s", "12"], facts.Select(f => f.Text));
    }

    [Fact]
    public void A_template_fills_each_field_and_is_used_only_when_every_field_is_there()
    {
        var mapping = """{"path":"r","id":"segment_id","text":["{date}: {summary}","{date}: {text}"]}""";

        var facts = Read("""{"r":[{"segment_id":"a","date":"D1","summary":"S1","text":"T1"},{"segment_id":"b","date":"D2","summary":null,"text":"T2"},{"segment_id":"c","date":"D3","summary":"","text":"T3"},{"segment_id":"d","date":"D4","text":"T4"},{"segment_id":"e","summary":"S5","text":"T5"},{"segment_id":"f","date":"D6","summary":null,"text":""}]}""", mapping);

        Assert.Equal([new("a", "D1: S1"), new("b", "D2: T2"), new("c", "D3: T3"), new("d", "D4: T4")], facts);
    }

    [Fact]
    public void Numbers_are_written_as_json_writes_them_and_a_template_may_repeat_a_field()
    {
        var facts = Read("""[{"score":0.031,"n":5,"t":"x"}]""", """{"text":"{t} {t} score {score} n {n}"}""");

        Assert.Equal("x x score 0.031 n 5", Assert.Single(facts).Text);
    }

    [Fact]
    public void A_field_may_hold_any_character_but_a_brace()
    {
        var facts = Read("""[{"first mention":"early","a.b":"dotted"}]""", """{"text":["{first mention} / {a.b}"]}""");

        Assert.Equal("early / dotted", Assert.Single(facts).Text);
    }

    [Fact]
    public void A_text_entry_may_be_one_string_and_the_first_to_qualify_wins()
    {
        Assert.Equal(["body"], Read("""[{"title":"t","body":"body"}]""", """{"text":"body"}""").Select(f => f.Text));
        Assert.Equal(["b"], Read("""[{"a":"","b":"b","c":"c"}]""", """{"text":["a","b","c"]}""").Select(f => f.Text));
    }

    [Fact]
    public void The_text_format_takes_the_whole_first_text_block_as_one_fact()
    {
        var reply = new CallToolResult { Content = [new TextContentBlock { Text = "Deploys go through GitOps." }, new TextContentBlock { Text = "second block" }] };

        var fact = Assert.Single(RecallResults.Read(reply, Mapping("""{"format":"text"}""")));

        Assert.Equal(("Deploys go through GitOps.", Sha12("Deploys go through GitOps.")), (fact.Text, fact.Id));
    }

    [Fact]
    public void The_text_format_ignores_structured_content_and_an_empty_block_is_no_fact()
    {
        var reply = new CallToolResult { Content = [new TextContentBlock { Text = "" }], StructuredContent = Json("""{"result":"x"}""") };

        Assert.Empty(RecallResults.Read(reply, Mapping("""{"format":"text"}""")));
        Assert.Throws<McpMemoryException>(() => RecallResults.Read(new CallToolResult { Content = [] }, Mapping("""{"format":"text"}""")));
    }
}
