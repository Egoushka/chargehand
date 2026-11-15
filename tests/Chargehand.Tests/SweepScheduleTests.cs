using Chargehand.Automation;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>When the sweep's clock fires, and what a restart catches up.</summary>
public class SweepScheduleTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(60);

    private static SweepSettings Utc(string[] at, DayOfWeek[]? days = null, string zone = "UTC") => new(true, at, days, zone);

    private static DateTimeOffset At(int day, int hour, int minute) => new(2026, 11, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void The_next_slot_is_the_first_time_after_now_on_a_day_that_runs()
    {
        var s = Utc(["08:00", "15:00", "20:00"], [DayOfWeek.Monday, DayOfWeek.Wednesday]);   // 2026-11-16 is a Monday

        Assert.Equal(At(16, 15, 0), SweepSchedule.NextAfter(s, At(16, 8, 0)));
        Assert.Equal(At(18, 8, 0), SweepSchedule.NextAfter(s, At(16, 20, 0)));
        Assert.Equal(At(23, 8, 0), SweepSchedule.NextAfter(s, At(18, 20, 30)));
    }

    [Fact]
    public void No_day_means_no_next_slot() => Assert.Null(SweepSchedule.NextAfter(Utc(["08:00"], []), At(16, 7, 0)));

    [Fact]
    public void Missed_by_20_minutes_runs_once_at_start()
    {
        var s = Utc(["08:00", "15:00"]);

        Assert.True(SweepSchedule.MissedWithin(s, At(16, 8, 5), At(16, 15, 20), Window));
        // The tick after that sweep started has the new last run, so the same slot does not fire twice.
        Assert.False(SweepSchedule.MissedWithin(s, At(16, 15, 20), At(16, 15, 21), Window));
    }

    [Fact]
    public void Missed_by_90_minutes_does_not() => Assert.False(SweepSchedule.MissedWithin(Utc(["08:00", "15:00"]), At(16, 8, 5), At(16, 16, 30), Window));

    [Fact]
    public void A_slot_on_a_day_that_does_not_run_is_not_missed() =>
        Assert.False(SweepSchedule.MissedWithin(Utc(["15:00"], [DayOfWeek.Tuesday]), At(16, 8, 0), At(16, 15, 20), Window));

    [Fact]
    public void Dst_day_runs_each_slot_once()
    {
        // Europe/Berlin springs forward on 2026-03-29: 02:00 becomes 03:00, so 02:30 does not exist.
        var s = Utc(["01:30", "02:30", "08:00"], zone: "Europe/Berlin");
        var fired = new List<DateTimeOffset>();
        var from = new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero);
        var last = from;
        for (var now = from; now < from.AddDays(2); now = now.AddMinutes(1))
        {
            if (!SweepSchedule.MissedWithin(s, last, now, Window))
                continue;
            fired.Add(now);
            last = now;
        }

        var day = fired.Where(f => f >= new DateTimeOffset(2026, 3, 28, 22, 0, 0, TimeSpan.Zero) && f < new DateTimeOffset(2026, 3, 29, 22, 0, 0, TimeSpan.Zero)).ToList();
        Assert.Equal(
            [new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 29, 6, 0, 0, TimeSpan.Zero)],
            day);
    }

    [Fact]
    public void The_next_slot_after_the_missing_hour_is_the_one_after_the_gap() =>
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), SweepSchedule.NextAfter(Utc(["01:30", "02:30", "08:00"], zone: "Europe/Berlin"), new DateTimeOffset(2026, 3, 29, 0, 31, 0, TimeSpan.Zero)));
}
