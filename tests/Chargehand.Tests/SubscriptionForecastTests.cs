using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Tests;

/// <summary>Issue 441, ADR 0098: a subscription's five-hour and seven-day windows forecast from a fixture call log: when each resets, when each is forecast to close first, and why a
/// large task should wait for a reset.</summary>
public sealed class SubscriptionForecastTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FiveResets = Now.AddHours(2);
    private static readonly DateTimeOffset SevenResets = Now.AddDays(3);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static (DateTimeOffset At, SubscriptionUsage Usage) Reading(double minutesAgo, double? five, double? seven, DateTimeOffset? fiveResets = null) =>
        (Now.AddMinutes(-minutesAgo), SubscriptionUsage.Of(five is { } f ? new UsageWindow(f, fiveResets ?? FiveResets) : null, seven is { } s ? new UsageWindow(s, SevenResets) : null)!);

    [Fact]
    public void A_log_with_no_readings_forecasts_nothing()
    {
        Assert.Null(UsageForecasts.Of([], Now));
        Assert.Null(UsageForecasts.LargeTaskHoldReason(null, Now));
    }

    [Fact]
    public void A_window_reports_its_latest_reading_and_its_reset_and_a_window_the_log_never_saw_stays_null()
    {
        var forecast = UsageForecasts.Of([Reading(30, 0.10, null)], Now)!;

        Assert.Null(forecast.SevenDay);
        Assert.Equal(new UsageWindowForecast(SubscriptionUsage.FiveHourName, 0.10, FiveResets, 0, null), forecast.FiveHour);
    }

    [Fact]
    public void A_climbing_window_is_forecast_to_close_at_the_rate_it_rose_over_the_readings_since_its_last_reset()
    {
        // 0.40 -> 0.70 over 60 minutes: 0.30 an hour, so the remaining 0.30 takes one more hour; the reset is two hours away.
        var forecast = UsageForecasts.Of([Reading(60, 0.40, null), Reading(30, 0.55, null), Reading(0, 0.70, null)], Now)!.FiveHour!;

        Assert.Equal(0.30, forecast.RatePerHour, 6);
        Assert.Equal(Now.AddHours(1), forecast.ClosesAt);
    }

    [Fact]
    public void A_window_that_resets_before_it_would_close_is_not_forecast_to_close()
    {
        var forecast = UsageForecasts.Of([Reading(60, 0.10, null), Reading(0, 0.20, null)], Now)!.FiveHour!;

        Assert.Equal(0.10, forecast.RatePerHour, 6);
        Assert.Null(forecast.ClosesAt);
    }

    [Fact]
    public void A_flat_or_falling_window_has_no_close()
    {
        Assert.Null(UsageForecasts.Of([Reading(60, 0.50, null), Reading(0, 0.50, null)], Now)!.FiveHour!.ClosesAt);
        Assert.Null(UsageForecasts.Of([Reading(60, 0.60, null), Reading(0, 0.50, null)], Now)!.FiveHour!.ClosesAt);
    }

    [Fact]
    public void Readings_from_before_the_window_last_reset_do_not_set_the_rate()
    {
        var earlier = FiveResets.AddHours(-5);
        var forecast = UsageForecasts.Of([Reading(200, 0.95, null, earlier), Reading(10, 0.05, null), Reading(0, 0.06, null)], Now)!.FiveHour!;

        Assert.Equal(0.06 - 0.05, forecast.RatePerHour * (10.0 / 60), 6);
        Assert.Null(forecast.ClosesAt);
    }

    [Fact]
    public void A_reset_that_has_passed_with_no_newer_reading_rolls_forward_by_whole_windows_and_carries_no_rate()
    {
        var passed = Now.AddMinutes(-30);
        var forecast = UsageForecasts.Of([Reading(90, 0.30, null, passed), Reading(60, 0.80, null, passed)], Now)!.FiveHour!;

        Assert.Equal(passed.AddHours(5), forecast.ResetsAt);
        Assert.Equal(0, forecast.RatePerHour);
        Assert.Null(forecast.ClosesAt);
    }

    [Fact]
    public void A_window_already_at_its_cap_closes_now()
    {
        Assert.Equal(Now, UsageForecasts.Of([Reading(0, 1.02, null)], Now)!.FiveHour!.ClosesAt);
    }

    [Fact]
    public void A_large_task_waits_when_a_window_is_forecast_to_close_within_the_horizon_and_not_when_the_close_is_further_off()
    {
        var farOff = UsageForecasts.Of([Reading(60, 0.40, null), Reading(0, 0.70, null)], Now)!;
        Assert.Null(UsageForecasts.LargeTaskHoldReason(farOff, Now));

        var imminent = UsageForecasts.Of([Reading(60, 0.40, null), Reading(0, 0.94, null)], Now)!;
        Assert.Contains("five_hour window is forecast to close at 2026-11-14T12:06:40Z before it resets at 2026-11-14T14:00:00Z", UsageForecasts.LargeTaskHoldReason(imminent, Now), StringComparison.Ordinal);
        Assert.Equal(imminent.FiveHour, imminent.ClosingWithin(Now, UsageForecasts.LargeTaskHorizon));
        Assert.Null(imminent.ClosingWithin(Now, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task The_run_log_serves_the_subscription_readings_since_a_time_oldest_first_and_skips_calls_without_one()
    {
        var log = new JsonlRunLog(Path.Combine(_dir.Path, "log.jsonl"));
        await log.AppendAsync(Call(Now.AddMinutes(-30), Reading(30, 0.55, 0.12).Usage), CancellationToken.None);
        await log.AppendAsync(Call(Now.AddMinutes(-60), Reading(60, 0.40, 0.11).Usage), CancellationToken.None);
        await log.AppendAsync(Call(Now.AddMinutes(-45), null), CancellationToken.None);
        await log.AppendAsync(Call(Now.AddDays(-9), Reading(0, 0.99, 0.99).Usage), CancellationToken.None);

        var readings = await log.SubscriptionUsageAsync(Now.AddDays(-7), CancellationToken.None);

        Assert.Equal([0.40, 0.55], readings.Select(r => r.Usage.FiveHour!.Utilization));
        var forecast = UsageForecasts.Of(readings, Now)!;
        Assert.Equal(0.30, forecast.FiveHour!.RatePerHour, 6);
        Assert.Equal(0.12, forecast.SevenDay!.Utilization);
    }

    private static CallRecord Call(DateTimeOffset started, SubscriptionUsage? usage) =>
        new("r1", "worker", "worker", "s1", "m1", "p/small", started, 1000, null, null, new PromptChain([], new AsSent("2.0.16", "build", "p/m", "2026-09-26")),
            Credential: "subscription", SubscriptionUsage: usage);
}
