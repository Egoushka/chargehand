using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;

namespace Chargehand.Tests;

/// <summary>CHARGEHAND-216, ADR 0076: the month's spend projected at the trailing 7-day rate against <c>max_usd_per_month</c>.</summary>
public class SpendForecastTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_month_so_far_plus_the_last_7_days_rate_until_the_month_ends_passes_the_cap_and_says_when()
    {
        // 16.5 days are left in October at noon on the 15th.
        var forecast = SpendForecasts.Of(16m, 14m, 40m, Now);
        Assert.Equal((2m, 49m, 40m, true), (forecast.RatePerDay, forecast.ProjectedUsd, forecast.CapUsd, forecast.OverCap));
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero), forecast.CapReachedAt);
    }

    [Fact]
    public void Under_the_cap_it_names_no_day_and_with_no_cap_nothing_is_over()
    {
        var under = SpendForecasts.Of(10m, 7m, 60m, Now);
        Assert.Equal((26.5m, false, (DateTimeOffset?)null), (under.ProjectedUsd, under.OverCap, under.CapReachedAt));
        var none = SpendForecasts.Of(10m, 7m, null, Now);
        Assert.Equal((false, (DateTimeOffset?)null), (none.OverCap, none.CapReachedAt));
        var idle = SpendForecasts.Of(0m, 0m, 60m, Now);
        Assert.Equal((0m, 0m, false), (idle.RatePerDay, idle.ProjectedUsd, idle.OverCap));
    }

    [Fact]
    public void A_cap_already_used_is_reached_now()
    {
        var used = SpendForecasts.Of(41m, 0m, 40m, Now);
        Assert.Equal((true, (DateTimeOffset?)Now), (used.OverCap, used.CapReachedAt));
    }

    [Fact]
    public async Task Accounts_me_and_the_accounts_list_carry_the_forecast()
    {
        var clock = new ManualTimeProvider();
        clock.Advance(Now - clock.GetUtcNow());
        AccountSettings alice = new("alice", MaxUsdPerMonth: 40m, AllowServerCredential: true);
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [alice], clock: clock);
        await Seed(s.Log, "a-recent", "alice", Now.AddDays(-2), 14m);
        await Seed(s.Log, "a-early", "alice", new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero), 2m);
        using var client = s.As(s.Keys.Create("alice", null).Key);

        var forecast = (await client.GetFromJsonAsync<JsonElement>("/v1/accounts/me")).GetProperty("forecast");
        Assert.Equal((2m, 49m, true), (forecast.GetProperty("rate_per_day").GetDecimal(), forecast.GetProperty("projected_usd").GetDecimal(), forecast.GetProperty("over_cap").GetBoolean()));
        var listed = (await s.Http.GetFromJsonAsync<JsonElement>("/v1/accounts")).GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "alice");
        Assert.Equal(40m, listed.GetProperty("forecast").GetProperty("cap_usd").GetDecimal());
    }

    internal static async Task Seed(JsonlRunLog log, string runId, string actor, DateTimeOffset finished, decimal usd)
    {
        var req = Runs.DraftRequest();
        await log.AppendAsync(new StartRecord(runId, finished.AddMinutes(-1), new string('0', 32), req, 1, null, "earlier-boot", actor), CancellationToken.None);
        var result = Runs.Completed() with { TaskId = runId, Usage = new Usage(100, 0, 0, 0, usd), Actor = actor };
        await log.AppendAsync(new RunRecord(runId, finished.AddMinutes(-1), finished, req.Context.Preset, null, null, result, Actor: actor), CancellationToken.None);
    }
}
