using Chargehand.Modules.Economics.Api;

namespace Chargehand.Modules.Economics.Tests;

/// <summary>The model router (ADR 0095): drain order, a provider's refusal as the only thing that takes a pool out, the daily allowance with carry-over as a warning, refills and expiry, escalation. November 2026 starts on a Sunday; all times are UTC.
/// These calls run over public data, which any model may see; the data-class gate is in <see cref="DataClassRoutingTests"/>.</summary>
public class ModelRouterTests
{
    private static readonly PoolSettings Free = new("free", PoolKinds.Free, ["free/small"]);
    private static readonly PoolSettings Bought = new("bought", PoolKinds.Fixed, ["paid/small", "paid/big"], TotalUsd: 30, Start: new DateOnly(2026, 11, 1), SpreadDays: 30);
    private static readonly PoolSettings Credit = new("credit", PoolKinds.Monthly, ["paid/small", "paid/big"], TotalUsd: 300, NoTrainingModels: ["*"]);
    private static readonly PoolSettings Subscription = new("subscription", PoolKinds.Weekly, ["paid/big"], RefillWeekday: "sunday", RefillTime: "09:00", NoTrainingModels: ["*"]);

    private static PoolsSettings Settings(params PoolSettings[] order) => new(Enabled: true, DefaultTier: "cheap",
        Tiers: [new("cheap", ["free/small", "paid/small"]), new("strong", ["paid/big"])],
        Routes: new Dictionary<string, string> { ["code"] = "cheap", ["code/L"] = "strong", ["review"] = "strong" },
        Order: order.Length == 0 ? [Free, Credit, Subscription, Bought] : order);

    private static ModelRouter Router(params PoolSettings[] order) => new(Settings(order));

    private static DateTimeOffset At(int day, int hour = 12, int minute = 0, int month = 11) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    private static PoolStanding Standing(PoolSettings pool, DateTimeOffset now, params PoolSpend[] spend) => PoolBalance.Of(pool, spend, now, TimeZoneInfo.Utc);

    private static RouteChoice Chosen(RouteDecision decision) => Assert.IsType<RouteChoice>(decision);

    [Fact]
    public void A_cheap_task_runs_on_the_free_pool_first()
    {
        var choice = Chosen(Router().Choose(0, DataClass.Public, [], At(10)));

        Assert.Equal(("free", "free/small", "cheap"), (choice.Pool, choice.Model, choice.Tier));
    }

    [Fact]
    public void A_free_pool_the_provider_refused_falls_through_to_the_next_pool_and_its_model()
    {
        var choice = Chosen(Router().Choose(0, DataClass.Public, [], At(10), new HashSet<string> { "free" }));

        Assert.Equal(("credit", "paid/small"), (choice.Pool, choice.Model));
    }

    [Fact]
    public void A_pool_that_does_not_pay_for_the_tier_is_skipped()
    {
        var choice = Chosen(Router().Choose(1, DataClass.Public, [], At(10)));

        Assert.Equal(("credit", "paid/big"), (choice.Pool, choice.Model));
    }

    [Fact]
    public void A_pool_whose_balance_is_spent_only_warns_and_keeps_its_place_in_the_order()
    {
        PoolSpend[] spend = [new("credit", At(2), 300)];

        var choice = Chosen(Router().Choose(1, DataClass.Public, spend, At(10)));

        Assert.Equal(("credit", "empty"), (choice.Pool, choice.Warning));
        var empty = Standing(Credit, At(10), spend);
        Assert.Equal((0m, true, "empty"), (empty.RemainingUsd, empty.HasRoom, empty.Warning));
        Assert.Null(empty.Why);
    }

    [Fact]
    public void Pools_the_provider_refused_fall_through_in_order_and_bought_credit_comes_last()
    {
        var last = Chosen(Router().Choose(0, DataClass.Public, [], At(10), new HashSet<string> { "free", "credit" }));

        Assert.Equal(("bought", "paid/small"), (last.Pool, last.Model));
    }

    [Fact]
    public void A_refused_subscription_and_credit_leave_the_bought_pool_for_the_strong_tier()
    {
        var choice = Chosen(Router().Choose(1, DataClass.Public, [], At(10), new HashSet<string> { "credit", "subscription" }));

        Assert.Equal(("bought", "paid/big"), (choice.Pool, choice.Model));
    }

    [Fact]
    public void The_daily_allowance_is_the_remainder_over_the_days_left()
    {
        var first = Standing(Bought, At(1));

        Assert.Equal((1m, 1m, 30m), (first.AllowanceUsd, first.RoomUsd, first.RemainingUsd));
        Assert.Equal(10m, Standing(Credit, At(1)).AllowanceUsd);
    }

    [Fact]
    public void A_day_that_spends_less_carries_the_rest_into_every_later_allowance()
    {
        Assert.Equal(30m / 29, Standing(Bought, At(2)).AllowanceUsd);
        Assert.Equal(29.5m / 29, Standing(Bought, At(2), new PoolSpend("bought", At(1), 0.5m)).AllowanceUsd);
    }

    [Fact]
    public void A_day_that_overshoots_shrinks_the_later_allowances()
    {
        var next = Standing(Bought, At(2), new PoolSpend("bought", At(1), 3));

        Assert.Equal(27m / 29, next.AllowanceUsd);
        Assert.True(next.AllowanceUsd < 1m);
    }

    [Fact]
    public void A_pool_whose_allowance_is_spent_today_still_takes_the_call_and_warns()
    {
        PoolSpend[] spend = [new("credit", At(1, 10), 10)];

        var spent = Standing(Credit, At(1), spend);
        Assert.Equal((0m, true, "today's allowance is spent"), (spent.RoomUsd, spent.HasRoom, spent.Warning));
        var choice = Chosen(Router().Choose(1, DataClass.Public, spend, At(1)));
        Assert.Equal(("credit", "today's allowance is spent"), (choice.Pool, choice.Warning));
        Assert.Null(Standing(Credit, At(2, 0), spend).Warning);
    }

    [Fact]
    public void A_monthly_pool_refills_on_its_day_and_forgets_the_last_periods_spend()
    {
        var late = Credit with { RefillDay = 15 };
        PoolSpend[] spend = [new("credit", At(14), 300)];

        var before = Standing(late, At(14, 23, 59), spend);
        Assert.Equal((At(15, 0, 0, 10), At(15, 0), 0m, "empty"), (before.PeriodStart!.Value, before.Refill!.Value, before.RemainingUsd, before.Warning));

        var after = Standing(late, At(15, 0), spend);
        Assert.Equal((At(15, 0), At(15, 0, 0, 12), 300m, (string?)null), (after.PeriodStart!.Value, after.Refill!.Value, after.RemainingUsd, after.Warning));
    }

    [Fact]
    public void A_refill_day_past_the_months_end_falls_on_its_last_day()
    {
        var endOfMonth = Credit with { RefillDay = 31 };

        var november = Standing(endOfMonth, At(30));
        Assert.Equal((At(30, 0), At(31, 0, 0, 12)), (november.PeriodStart!.Value, november.Refill!.Value));
        Assert.Equal((At(31, 0, 0, 10), At(30, 0)), (Standing(endOfMonth, At(29)).PeriodStart!.Value, Standing(endOfMonth, At(29)).Refill!.Value));
    }

    [Fact]
    public void A_weekly_pool_refills_on_sunday_at_its_time()
    {
        var before = Standing(Subscription, At(8, 8, 59));
        Assert.Equal((At(1, 9), At(8, 9)), (before.PeriodStart!.Value, before.Refill!.Value));

        var after = Standing(Subscription, At(8, 9));
        Assert.Equal((At(8, 9), At(15, 9)), (after.PeriodStart!.Value, after.Refill!.Value));
    }

    [Fact]
    public void An_unmetered_weekly_pool_always_has_room()
    {
        var standing = Standing(Subscription, At(4), new PoolSpend("subscription", At(4, 10), 500));

        Assert.Equal((true, (decimal?)null, (decimal?)null), (standing.HasRoom, standing.RemainingUsd, standing.RoomUsd));
    }

    [Fact]
    public void When_every_pool_is_out_the_router_waits_for_a_refill_that_comes_before_tomorrow()
    {
        var metered = Subscription with { QuotaUsd = 7 };
        PoolSpend[] spend = [new("subscription", At(7), 7)];
        var refused = new HashSet<string> { "subscription" };

        var wait = Assert.IsType<RouteWait>(Router(metered).Choose(1, DataClass.Public, spend, At(8, 8), refused));

        Assert.Equal(At(8, 9), wait.Until);
        var standing = Assert.Single(wait.Standings);
        Assert.Equal((false, "exhausted"), (standing.HasRoom, standing.Why));
        Assert.True(Chosen(Router(metered).Choose(1, DataClass.Public, spend, At(8, 9))).RoomUsd > 0);
    }

    [Fact]
    public void A_metered_pool_that_is_over_its_quota_but_not_refused_is_not_waited_on()
    {
        var metered = Subscription with { QuotaUsd = 7 };
        PoolSpend[] spend = [new("subscription", At(7), 7)];

        var choice = Chosen(Router(metered).Choose(1, DataClass.Public, spend, At(7, 12)));

        Assert.Equal(("subscription", "empty"), (choice.Pool, choice.Warning));
    }

    [Fact]
    public void A_fixed_pool_paces_to_its_expiry_when_that_comes_first_and_is_gone_after_it()
    {
        var expiring = Bought with { Expires = new DateOnly(2026, 11, 5) };

        var lastDay = Standing(expiring, At(4));
        Assert.Equal((At(5, 0), 30m), (lastDay.Horizon!.Value, lastDay.AllowanceUsd));

        var gone = Standing(expiring, At(5, 0));
        Assert.Equal((false, "expired"), (gone.HasRoom, gone.Why));
    }

    [Fact]
    public void A_fixed_pool_has_no_room_before_its_start()
    {
        var early = Standing(Bought, At(31, 12, 0, 10));

        Assert.Equal((false, "not started"), (early.HasRoom, early.Why));
    }

    [Fact]
    public void A_fixed_pool_past_its_spread_with_money_left_offers_all_of_it()
    {
        var late = Standing(Bought, At(2, 12, 0, 12), new PoolSpend("bought", At(10), 20));

        Assert.Equal(((DateTimeOffset?)null, (decimal?)10m, (decimal?)10m), (late.Horizon, late.AllowanceUsd, late.RoomUsd));
    }

    [Fact]
    public void A_free_pool_with_a_daily_quota_warns_at_it_and_stops_warning_the_next_day()
    {
        var quota = Free with { DailyRequests = 2 };
        PoolSpend[] spend = [new("free", At(3, 1), 0), new("free", At(3, 2), 0)];

        var over = Standing(quota, At(3), spend);
        Assert.Equal((true, "today's requests are used"), (over.HasRoom, over.Warning));
        Assert.Null(Standing(quota, At(4), spend).Warning);
        Assert.Null(Standing(quota, At(3), spend[0]).Warning);
    }

    [Fact]
    public void One_failed_check_keeps_the_tier_and_the_second_moves_it_one_up()
    {
        var tier = new TaskTier(0);

        var once = tier.Failed(escalateAfter: 2, topTier: 2);
        var twice = once.Failed(2, 2);

        Assert.Equal(new TaskTier(0, 1), once);
        Assert.Equal(new TaskTier(1, 0), twice);
        Assert.Equal(new TaskTier(2, 0), twice.Failed(2, 2).Failed(2, 2));
    }

    [Fact]
    public void A_pass_between_failures_resets_the_count_and_the_tier_never_moves_down()
    {
        Assert.Equal(new TaskTier(0, 1), new TaskTier(0).Failed(2, 2).Passed().Failed(2, 2));
        Assert.Equal(new TaskTier(1, 0), new TaskTier(1, 1).Passed());
        Assert.Equal(new TaskTier(1, 0), new TaskTier(1, 1).Failed(2, 1));
    }

    [Fact]
    public void The_tier_comes_from_kind_and_size_then_kind_then_the_default()
    {
        var router = Router();

        Assert.Equal(1, router.TierFor("code", "L"));
        Assert.Equal(0, router.TierFor("code", "S"));
        Assert.Equal(1, router.TierFor("review"));
        Assert.Equal(0, router.TierFor("intake"));
        Assert.Equal(1, new ModelRouter(Settings() with { DefaultTier = "strong" }).TierFor("intake"));
    }

    [Fact]
    public void A_block_that_is_off_and_empty_has_no_problems()
    {
        Assert.Empty(new PoolsSettings().Problems());
        Assert.Empty(Settings().Problems());
    }

    [Fact]
    public void A_broken_block_names_each_problem()
    {
        var broken = Settings(Bought with { SpreadDays = 0 }, Subscription with { RefillWeekday = "someday" }, Credit with { Name = "bought" }) with
        {
            TimeZone = "Nowhere/Place",
            EscalateAfter = 0,
            DefaultTier = "missing",
            Tiers = [new("cheap", ["free/small"]), new("strong", ["paid/big"])],
        };

        var problems = broken.Problems().ToList();

        Assert.Contains("time_zone 'Nowhere/Place' is not a time zone this machine knows", problems);
        Assert.Contains("escalate_after must be at least 1", problems);
        Assert.Contains("pool 'bought' is named more than once", problems);
        Assert.Contains("pool 'bought': spread_days must be at least 1", problems);
        Assert.Contains("pool 'subscription': a weekly pool needs refill_weekday, a day of the week in English", problems);
        Assert.Contains("default_tier 'missing' is not a tier", problems);
        Assert.Contains("tier 'cheap': no pool pays for any of its models", problems);
    }

    private static readonly PoolSettings Paced = Subscription with { QuotaUsd = 700, PaceDaily = true };

    [Fact]
    public void A_paced_pool_whose_day_is_spent_hands_the_call_to_the_next_pool()
    {
        // Tuesday 10th: period began Sunday 8th 09:00, six days to the refill, so the allowance is 700 / 6 = 116.67 and 120 is spent.
        var choice = Chosen(Router(Paced, Credit).Choose(1, DataClass.Public, [new("subscription", At(10, 8), 120)], At(10)));

        Assert.Equal(("credit", "paid/big"), (choice.Pool, choice.Model));
    }

    [Fact]
    public void A_pool_without_pace_daily_keeps_the_call_with_a_warning_when_its_day_is_spent()
    {
        var choice = Chosen(Router(Paced with { PaceDaily = false }, Credit).Choose(1, DataClass.Public, [new("subscription", At(10, 8), 120)], At(10)));

        Assert.Equal(("subscription", "today's allowance is spent"), (choice.Pool, choice.Warning));
    }

    [Fact]
    public void A_paced_pool_with_room_today_takes_the_call()
    {
        var choice = Chosen(Router(Paced, Credit).Choose(1, DataClass.Public, [new("subscription", At(10, 8), 100)], At(10)));

        Assert.Equal(("subscription", null), (choice.Pool, choice.Warning));
    }

    [Fact]
    public void A_paced_pool_is_still_used_when_no_other_pool_can_take_the_call()
    {
        var choice = Chosen(Router(Paced, Credit).Choose(1, DataClass.Public, [new("subscription", At(10, 8), 120)], At(10), new HashSet<string> { "credit" }));

        Assert.Equal(("subscription", "today's allowance is spent"), (choice.Pool, choice.Warning));
    }

    [Fact]
    public void A_paced_pool_the_provider_refused_is_out_not_soft()
    {
        var decision = Router(Paced).Choose(1, DataClass.Public, [new("subscription", At(10, 8), 120)], At(10), new HashSet<string> { "subscription" });

        Assert.IsType<RouteWait>(decision);
    }

    [Fact]
    public void A_paced_pool_gets_its_allowance_back_the_next_day_with_what_was_left_carried_over()
    {
        // Wednesday 11th: 580 left over five days (4.375 rounded up) is 116, nothing spent yet.
        var standing = Standing(Paced, At(11), new PoolSpend("subscription", At(10, 8), 120));

        Assert.Equal((116m, false), (standing.AllowanceUsd, standing.Paced));
    }

    [Fact]
    public void Pace_daily_needs_an_amount_to_pace()
    {
        var problems = Settings(Subscription with { PaceDaily = true }, Free with { PaceDaily = true }).Problems().ToList();

        Assert.Contains("pool 'subscription': pace_daily needs an amount to pace: total_usd, or quota_usd on a weekly pool", problems);
        Assert.Contains("pool 'free': pace_daily needs an amount to pace: total_usd, or quota_usd on a weekly pool", problems);
        Assert.DoesNotContain(Settings(Paced).Problems(), p => p.Contains("pace_daily"));
    }
}
