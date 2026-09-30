using System.Text.Json;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>ADR 0039: a driven session that loops, idles or runs on must end. Four detectors, each with its own reason.</summary>
public class StallDetectorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static StallDetector Detector(int maxTurns = 80) =>
        new(noProgress: TimeSpan.FromMinutes(10), repeatLimit: 5, maxTurns: maxTurns, wallClock: TimeSpan.FromMinutes(45), Start);

    private static JsonElement Assistant(string id, params (string Tool, string Input)[] calls) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "assistant",
            message = new { id, content = calls.Select(c => new { type = "tool_use", name = c.Tool, input = JsonDocument.Parse(c.Input).RootElement }).ToArray() },
        });

    [Fact]
    public void No_event_for_the_limit_is_a_stall_and_any_event_resets_it()
    {
        var d = Detector();
        Assert.Null(d.Check(Start.AddMinutes(9)));
        d.OnEvent(Assistant("m1"), Start.AddMinutes(9));
        Assert.Null(d.Check(Start.AddMinutes(18)));
        Assert.Equal(StallReason.NoProgress, d.Check(Start.AddMinutes(20)));
    }

    [Fact]
    public void The_same_tool_call_five_times_in_a_row_is_a_loop()
    {
        var d = Detector();
        for (var i = 1; i <= 4; i++)
            d.OnEvent(Assistant($"m{i}", ("Bash", """{"command":"dotnet test"}""")), Start.AddSeconds(i));
        Assert.Null(d.Check(Start.AddSeconds(5)));
        d.OnEvent(Assistant("m5", ("Bash", """{"command":"dotnet test"}""")), Start.AddSeconds(6));
        Assert.Equal(StallReason.Repeating, d.Check(Start.AddSeconds(7)));
    }

    [Fact]
    public void A_different_call_in_between_resets_the_repeat_counter()
    {
        var d = Detector();
        for (var i = 1; i <= 4; i++)
            d.OnEvent(Assistant($"a{i}", ("Bash", """{"command":"dotnet test"}""")), Start.AddSeconds(i));
        d.OnEvent(Assistant("b", ("Edit", """{"file":"x"}""")), Start.AddSeconds(5));
        for (var i = 1; i <= 4; i++)
            d.OnEvent(Assistant($"c{i}", ("Bash", """{"command":"dotnet test"}""")), Start.AddSeconds(5 + i));
        Assert.Null(d.Check(Start.AddSeconds(10)));
    }

    [Fact]
    public void The_same_tool_with_different_input_is_not_a_loop()
    {
        var d = Detector();
        for (var i = 1; i <= 8; i++)
            d.OnEvent(Assistant($"m{i}", ("Read", $$"""{"file":"f{{i}}.cs"}""")), Start.AddSeconds(i));
        Assert.Null(d.Check(Start.AddSeconds(9)));
    }

    [Fact]
    public void More_assistant_messages_than_the_maximum_is_a_stall()
    {
        var d = Detector(maxTurns: 2);
        d.OnEvent(Assistant("m1"), Start.AddSeconds(1));
        d.OnEvent(Assistant("m2"), Start.AddSeconds(2));
        Assert.Null(d.Check(Start.AddSeconds(3)));
        d.OnEvent(Assistant("m3"), Start.AddSeconds(4));
        Assert.Equal(StallReason.TurnLimit, d.Check(Start.AddSeconds(5)));
        Assert.Equal(3, d.Turns);
    }

    [Fact]
    public void One_message_streamed_in_several_events_counts_once()
    {
        var d = Detector(maxTurns: 2);
        for (var i = 0; i < 6; i++)
            d.OnEvent(Assistant("same"), Start.AddSeconds(i));
        Assert.Equal(1, d.Turns);
        Assert.Null(d.Check(Start.AddSeconds(7)));
    }

    [Fact]
    public void The_wall_clock_ends_a_session_that_keeps_making_progress()
    {
        var d = Detector();
        for (var m = 1; m <= 44; m++)
            d.OnEvent(Assistant($"m{m}", ("Read", $$"""{"file":"{{m}}"}""")), Start.AddMinutes(m));
        Assert.Null(d.Check(Start.AddMinutes(44.5)));
        d.OnEvent(Assistant("late", ("Read", """{"file":"late"}""")), Start.AddMinutes(45.5));
        Assert.Equal(StallReason.WallClock, d.Check(Start.AddMinutes(45.6)));
    }

    [Fact]
    public void An_event_with_no_message_still_counts_as_progress_and_never_throws()
    {
        var d = Detector();
        d.OnEvent(JsonDocument.Parse("""{"type":"system","subtype":"init"}""").RootElement, Start.AddMinutes(5));
        d.OnEvent(JsonDocument.Parse("""{"type":"assistant"}""").RootElement, Start.AddMinutes(6));
        d.OnEvent(JsonDocument.Parse("[]").RootElement, Start.AddMinutes(7));
        Assert.Null(d.Check(Start.AddMinutes(16)));
    }
}
