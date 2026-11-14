using Chargehand.Contracts;
using Chargehand.Modules.Economics.Api;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Modules.Economics.Tests;

/// <summary>A subscription's weekly cap paced by the provider's utilization reading: one day's share, unused share carried forward, a spent day handed to the next pool or deferred to tomorrow.
/// The window below starts Sunday 8 November 09:00 UTC and resets the next Sunday.</summary>
public class WeeklyPacingTests
{
    private const double Seventh = 1.0 / 7;
    private static readonly DateTimeOffset Reset = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int day, int hour = 12, int minute = 0) => new(2026, 11, day, hour, minute, 0, TimeSpan.Zero);

    private static UsageWindow Used(double fraction, DateTimeOffset? resets = null) => new(fraction, resets ?? Reset);

    [Theory]
    [InlineData(8, 1)]
    [InlineData(10, 3)]
    [InlineData(14, 7)]
    public void The_allowance_grows_by_one_share_a_day_so_unused_share_is_carried_forward(int day, int days) =>
        Assert.Equal(Seventh * days, WeeklyPacing.Allowance(Used(0), Seventh, At(day), TimeZoneInfo.Utc)!.Value, 9);

    [Fact]
    public void The_allowance_never_passes_the_whole_cap()
    {
        Assert.Equal(1.0, WeeklyPacing.Allowance(Used(0), 0.3, At(14), TimeZoneInfo.Utc));
        Assert.Equal(1.0, WeeklyPacing.Allowance(Used(0), Seventh, At(15, 8), TimeZoneInfo.Utc));
    }

    [Fact]
    public void A_day_is_spent_at_its_allowance_and_not_just_under_it()
    {
        Assert.True(WeeklyPacing.Spent(Used(3 * Seventh), Seventh, At(10), TimeZoneInfo.Utc));
        Assert.False(WeeklyPacing.Spent(Used(3 * Seventh - 0.01), Seventh, At(10), TimeZoneInfo.Utc));
        Assert.True(WeeklyPacing.Spent(Used(1.0), Seventh, At(14), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Midnight_in_the_routers_zone_starts_the_next_day()
    {
        var plus3 = TimeZoneInfo.CreateCustomTimeZone("plus3", TimeSpan.FromHours(3), "plus3", "plus3");
        var lateUtc = At(9, 21, 30);

        Assert.Equal(2 * Seventh, WeeklyPacing.Allowance(Used(0), Seventh, lateUtc, TimeZoneInfo.Utc)!.Value, 9);
        Assert.Equal(3 * Seventh, WeeklyPacing.Allowance(Used(0), Seventh, lateUtc, plus3)!.Value, 9);
    }

    [Fact]
    public void The_window_start_counts_in_the_zone_too()
    {
        var minus8 = TimeZoneInfo.CreateCustomTimeZone("minus8", TimeSpan.FromHours(-8), "minus8", "minus8");

        // Window start 8 Nov 09:00 UTC is 01:00 on the 8th there; 9 Nov 07:00 UTC is 23:00 on the 8th, still day one.
        Assert.Equal(Seventh, WeeklyPacing.Allowance(Used(0), Seventh, At(9, 7), minus8)!.Value, 9);
        Assert.Equal(2 * Seventh, WeeklyPacing.Allowance(Used(0), Seventh, At(9, 8), minus8)!.Value, 9);
    }

    [Fact]
    public void A_reading_whose_window_has_reset_or_has_no_reset_time_paces_nothing()
    {
        Assert.Null(WeeklyPacing.Allowance(Used(0.9, Reset.AddDays(-7)), Seventh, At(10), TimeZoneInfo.Utc));
        Assert.Null(WeeklyPacing.Allowance(new UsageWindow(0.9), Seventh, At(10), TimeZoneInfo.Utc));
        Assert.False(WeeklyPacing.Spent(new UsageWindow(0.9), Seventh, At(10), TimeZoneInfo.Utc));
    }

    [Fact]
    public void A_cap_raised_mid_week_lowers_the_reading_and_reopens_the_day()
    {
        Assert.True(WeeklyPacing.Spent(Used(0.5), Seventh, At(10), TimeZoneInfo.Utc));
        Assert.False(WeeklyPacing.Spent(Used(0.5 * 100 / 150), Seventh, At(10), TimeZoneInfo.Utc));
    }

    [Fact]
    public void A_changed_share_applies_at_once()
    {
        Assert.True(WeeklyPacing.Spent(Used(0.45), Seventh, At(10), TimeZoneInfo.Utc));
        Assert.False(WeeklyPacing.Spent(Used(0.45), 0.2, At(10), TimeZoneInfo.Utc));
    }

    private static readonly PoolSettings Credit = new("credit", PoolKinds.Monthly, ["paid/big"], TotalUsd: 300, NoTrainingModels: ["*"]);
    private static readonly PoolSettings Subscription = new("subscription", PoolKinds.Weekly, ["paid/big"], RefillWeekday: "sunday", RefillTime: "09:00", PaceDaily: true, NoTrainingModels: ["*"]);

    private static ModelRouter Router(params PoolSettings[] order) => new(new PoolsSettings(Enabled: true, DefaultTier: "strong", Tiers: [new("strong", ["paid/big"])], Order: order));

    private static IReadOnlyDictionary<string, UsageWindow> Reading(double fraction) => new Dictionary<string, UsageWindow> { ["subscription"] = Used(fraction) };

    [Fact]
    public void A_spent_day_hands_the_call_to_the_next_pool_with_room()
    {
        var choice = Assert.IsType<RouteChoice>(Router(Subscription, Credit).Choose(0, DataClass.Public, [], At(10), null, Reading(0.5)));

        Assert.Equal("credit", choice.Pool);
    }

    [Fact]
    public void A_day_with_room_stays_on_the_subscription()
    {
        var choice = Assert.IsType<RouteChoice>(Router(Subscription, Credit).Choose(0, DataClass.Public, [], At(10), null, Reading(0.3)));

        Assert.Equal("subscription", choice.Pool);
    }

    [Fact]
    public void With_no_other_pool_a_spent_day_waits_for_the_next_day_and_is_open_then()
    {
        var router = Router(Subscription);

        var wait = Assert.IsType<RouteWait>(router.Choose(0, DataClass.Public, [], At(10), null, Reading(3 * Seventh)));
        Assert.Equal(At(11, 0), wait.Until);
        Assert.IsType<RouteChoice>(router.Choose(0, DataClass.Public, [], wait.Until, null, Reading(3 * Seventh)));
    }

    [Fact]
    public void No_reading_paces_nothing()
    {
        Assert.IsType<RouteChoice>(Router(Subscription).Choose(0, DataClass.Public, [], At(10)));
    }

    [Fact]
    public void Two_weeks_of_constant_demand_never_lock_a_whole_day_out()
    {
        var router = Router(Subscription);
        var windowEnd = Reset;
        double used = 0;
        var emptyDays = 0;
        for (var day = 0; day < 14; day++)
        {
            var admitted = false;
            for (var hour = 0; hour < 24; hour++)
            {
                var now = At(8, 0).AddDays(day).AddHours(hour);
                if (now >= windowEnd)
                {
                    windowEnd = windowEnd.AddDays(7);
                    used = 0;
                }
                var reading = new Dictionary<string, UsageWindow> { ["subscription"] = new(used, windowEnd) };
                if (router.Choose(0, DataClass.Public, [], now, null, reading) is RouteChoice)
                {
                    admitted = true;
                    used += 0.05;
                }
            }
            if (!admitted)
                emptyDays++;
        }

        Assert.Equal(0, emptyDays);
    }

    [Fact]
    public void The_share_must_be_a_fraction_and_belongs_to_a_paced_pool_without_a_dollar_quota()
    {
        var problems = new PoolsSettings(Enabled: true, Tiers: [new("strong", ["paid/big"])], Order:
        [
            Subscription with { DailyShare = 0 },
            Subscription with { Name = "b", DailyShare = 1.5 },
            Subscription with { Name = "c", PaceDaily = false, DailyShare = 0.2 },
            Subscription with { Name = "d", QuotaUsd = 700, DailyShare = 0.2 },
            Subscription with { Name = "e", DailyShare = 0.15 },
            Subscription with { Name = "f" },
        ]).Problems().ToList();

        Assert.Equal(4, problems.Count(p => p.Contains("daily_share")));
        Assert.DoesNotContain(problems, p => p.Contains("pool 'e'") || p.Contains("pool 'f'"));
    }
}
