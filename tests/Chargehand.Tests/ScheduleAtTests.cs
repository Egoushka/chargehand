using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0068: the schedule syntax and the next occurrence, all in UTC.</summary>
public class ScheduleAtTests
{
    private static DateTimeOffset At(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    private static ScheduleAt Parse(string text)
    {
        Assert.True(ScheduleAt.TryParse(text, out var at, out var problem), problem);
        return at;
    }

    [Theory]
    [InlineData("daily 06:30", 2026, 1, 1, 6, 29, 2026, 1, 1, 6, 30)]
    [InlineData("daily 06:30", 2026, 1, 1, 6, 30, 2026, 1, 2, 6, 30)]
    [InlineData("daily 06:30", 2026, 1, 1, 6, 31, 2026, 1, 2, 6, 30)]
    [InlineData("daily 06:30", 2026, 12, 31, 23, 59, 2027, 1, 1, 6, 30)]
    [InlineData("weekly mon 09:00", 2026, 1, 1, 0, 0, 2026, 1, 5, 9, 0)]
    [InlineData("weekly thu 09:00", 2026, 1, 1, 9, 0, 2026, 1, 8, 9, 0)]
    [InlineData("WEEKLY Sun 00:00", 2026, 1, 1, 0, 0, 2026, 1, 4, 0, 0)]
    [InlineData("monthly 1 03:00", 2026, 1, 1, 3, 0, 2026, 2, 1, 3, 0)]
    [InlineData("monthly 28 03:00", 2026, 1, 15, 0, 0, 2026, 1, 28, 3, 0)]
    [InlineData("monthly 28 03:00", 2026, 2, 28, 3, 1, 2026, 3, 28, 3, 0)]
    [InlineData("monthly 15 12:00", 2026, 12, 20, 0, 0, 2027, 1, 15, 12, 0)]
    public void The_next_occurrence_is_the_first_one_strictly_after_the_given_time(string text, int y, int mo, int d, int h, int mi, int ey, int emo, int ed, int eh, int emi) =>
        Assert.Equal(At(ey, emo, ed, eh, emi), Parse(text).Next(At(y, mo, d, h, mi)));

    [Fact]
    public void A_time_with_an_offset_is_read_in_utc() =>
        Assert.Equal(At(2026, 1, 2, 6, 0), Parse("daily 06:00").Next(new DateTimeOffset(2026, 1, 1, 23, 0, 0, TimeSpan.FromHours(-5))));

    [Theory]
    [InlineData("")]
    [InlineData("daily")]
    [InlineData("daily 6:30")]
    [InlineData("daily 24:00")]
    [InlineData("daily 12:60")]
    [InlineData("daily mon 12:00")]
    [InlineData("weekly 12:00")]
    [InlineData("weekly funday 12:00")]
    [InlineData("monthly 29 12:00")]
    [InlineData("monthly 0 12:00")]
    [InlineData("monthly mon 12:00")]
    [InlineData("hourly 12:00")]
    [InlineData("*/5 * * * *")]
    public void Anything_else_is_refused_with_a_reason(string text)
    {
        Assert.False(ScheduleAt.TryParse(text, out _, out var problem));
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Fact]
    public void A_null_text_is_refused() => Assert.False(ScheduleAt.TryParse(null, out _, out _));
}
