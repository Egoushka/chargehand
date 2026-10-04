using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>Whether a stored session stream kept to the change skill's steps (ADR 0039). The streams here are synthetic: tool calls only.</summary>
public class AdherenceTests
{
    private static string Call(string id, string name, string input) =>
        $$$"""{"type":"assistant","message":{"id":"m{{{id}}}","content":[{"type":"tool_use","id":"t{{{id}}}","name":"{{{name}}}","input":{{{input}}}}]}}""";

    private static string Research(string id) => Call(id, "mcp__chargehand__orchestrate", """{"context":{"preset":"default"}}""");
    private static string Review(string id) => Call(id, "mcp__chargehand__orchestrate", """{"context":{"preset":"review"}}""");
    private static string Write(string id) => Call(id, "Edit", """{"file_path":"a.py"}""");
    private static string Test(string id) => Call(id, "Bash", """{"command":"python3 -m unittest -q"}""");

    [Fact]
    public void Research_write_test_review_in_order_is_followed()
    {
        var report = Adherence.Check([Research("1"), Write("2"), Test("3"), Review("4")]);
        Assert.True(report.Followed);
        Assert.Equal(0, report.FixRounds);
    }

    [Theory]
    [InlineData("node --test 2>&1 | tail -200")]
    [InlineData("npx jest")]
    [InlineData("npx vitest run")]
    public void Node_test_commands_count_as_a_test_run(string command)
    {
        var test = Call("3", "Bash", $$"""{"command":"{{command}}"}""");
        Assert.True(Adherence.Check([Research("1"), Write("2"), test, Review("4")]).Followed);
    }

    [Fact]
    public void Two_fix_rounds_are_allowed_and_a_third_is_not()
    {
        string[] two = [Research("1"), Write("2"), Test("3"), Review("4"), Write("5"), Test("6"), Review("7"), Write("8"), Test("9"), Review("10")];
        Assert.True(Adherence.Check(two).Followed);
        var three = Adherence.Check([.. two, Write("11"), Test("12"), Review("13")]);
        Assert.False(three.Followed);
        Assert.Equal(3, three.FixRounds);
    }

    [Fact]
    public void A_session_that_writes_before_it_researches_broke_the_order()
    {
        var report = Adherence.Check([Write("1"), Research("2"), Test("3"), Review("4")]);
        Assert.False(report.Followed);
        Assert.Contains(report.Problems, p => p.Contains("out of order"));
    }

    [Fact]
    public void A_missing_step_is_named()
    {
        var report = Adherence.Check([Research("1"), Write("2"), Review("3")]);
        Assert.False(report.Followed);
        Assert.Contains("no test", report.Problems);
    }

    [Fact]
    public void A_tool_call_repeated_in_a_partial_message_counts_once()
    {
        var report = Adherence.Check([Research("1"), Write("2"), Test("3"), Review("4"), Review("4")]);
        Assert.Equal(1, report.Reviews);
    }

    [Fact]
    public void Other_lines_and_other_presets_are_ignored()
    {
        var other = Call("9", "mcp__chargehand__orchestrate", """{"context":{"preset":"cheap"}}""");
        var report = Adherence.Check(["not json", """{"type":"result"}""", other, Research("1"), Write("2"), Test("3"), Review("4")]);
        Assert.True(report.Followed);
    }

    [Fact]
    public void The_command_reports_the_share_and_is_not_conclusive_under_ten_sessions()
    {
        using var dir = new TempDir();
        var good = Path.Combine(dir.Path, "a.jsonl");
        File.WriteAllLines(good, [Research("1"), Write("2"), Test("3"), Review("4")]);
        var output = new StringWriter();
        Assert.Equal(0, AdherenceCli.Run([good], output, new StringWriter()));
        Assert.Contains("1 of 1 sessions followed", output.ToString());
        Assert.Contains("not conclusive", output.ToString());
    }

    [Fact]
    public void Ten_sessions_with_six_followed_exit_one()
    {
        using var dir = new TempDir();
        var good = Path.Combine(dir.Path, "good.jsonl");
        var bad = Path.Combine(dir.Path, "bad.jsonl");
        File.WriteAllLines(good, [Research("1"), Write("2"), Test("3"), Review("4")]);
        File.WriteAllLines(bad, [Write("1")]);
        var output = new StringWriter();
        Assert.Equal(1, AdherenceCli.Run([.. Enumerable.Repeat(good, 6), .. Enumerable.Repeat(bad, 4)], output, new StringWriter()));
        Assert.Contains("not met", output.ToString());
    }
}
