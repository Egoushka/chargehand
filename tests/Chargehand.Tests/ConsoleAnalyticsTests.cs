using System.Globalization;
using System.Net;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Tests;

/// <summary><c>GET /v1/analytics</c> (CHARGEHAND-209) and <see cref="LearningSeries"/>: finished tasks and runs over time.</summary>
public class ConsoleAnalyticsTests
{
    private static readonly DateTimeOffset Now = ConsoleRig.Now;

    private static AccountSettings Alice() => new("alice", Driven: true, AllowServerCredential: true);

    private static IReadOnlyList<GovernorCount> Checkpoint() => [new GovernorCount("spend", "checkpoint", 1, 1)];

    /// <summary>Thursday 15 and 14 Oct, Thursday 8 Oct and Sunday 20 Sep, owner and alice.</summary>
    private static async Task SeedAsync(ConsoleRig rig)
    {
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b1", "t1", at: Now.AddHours(-2), usd: 2m));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b1", "t2", LearningOutcome.Failed, at: Now.AddHours(-3), usd: 1m, governor: Checkpoint(), mode: "enforce"));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b2", "t1", actor: "alice", repository: "o/x", size: "S", at: Now.AddDays(-1), usd: 0.5m, continuations: 2, governor: Checkpoint()));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b3", "t1", LearningOutcome.Partial, "alice", "o/x", "L", Now.AddDays(-7), usd: null));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b4", "t1", LearningOutcome.NeedsInput, at: new DateTimeOffset(2026, 9, 20, 23, 0, 0, TimeSpan.Zero), usd: 3m));
    }

    private static List<JsonElement> Points(JsonElement body) => [.. body.GetProperty("tasks").GetProperty("points").EnumerateArray()];

    [Fact]
    public async Task Tasks_are_summed_per_day_with_pass_rate_spend_and_tokens_including_cache()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()]);
        await SeedAsync(rig);

        var body = await ConsoleRig.Get(rig.Owner, "/v1/analytics");

        Assert.Equal("analytics/v1", body.GetProperty("contract_version").GetString());
        Assert.Equal(("day", "none"), (body.GetProperty("bucket").GetString(), body.GetProperty("group_by").GetString()));
        var points = Points(body);
        Assert.Equal(["2026-09-20", "2026-10-08", "2026-10-14", "2026-10-15"], points.Select(p => p.GetProperty("bucket_start").GetDateTimeOffset().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        var today = points[^1];
        Assert.Equal((2, 1, 1, 0.5), (today.GetProperty("tasks").GetInt32(), today.GetProperty("completed").GetInt32(), today.GetProperty("failed").GetInt32(), today.GetProperty("pass_rate").GetDouble()));
        Assert.Equal(3m, today.GetProperty("usd_estimate").GetDecimal());
        Assert.Equal((2000, 400, 6000, 800, 62), (today.GetProperty("input_tokens").GetInt32(), today.GetProperty("output_tokens").GetInt32(), today.GetProperty("cache_read_tokens").GetInt32(),
            today.GetProperty("cache_write_tokens").GetInt32(), today.GetProperty("turns").GetInt32()));
        Assert.Equal(0, points[1].GetProperty("completed").GetInt32());
        Assert.Equal(1, points[1].GetProperty("partial").GetInt32());
        Assert.Equal(1, points[0].GetProperty("needs_input").GetInt32());
        var total = body.GetProperty("tasks").GetProperty("total");
        Assert.Equal((5, 2, 0.4), (total.GetProperty("tasks").GetInt32(), total.GetProperty("completed").GetInt32(), total.GetProperty("pass_rate").GetDouble()));
        Assert.Equal(6.5m, total.GetProperty("usd_estimate").GetDecimal());
        Assert.Equal(4, total.GetProperty("usd_estimate_known").GetInt32());
    }

    [Fact]
    public async Task Buckets_are_days_utc_weeks_from_monday_and_months()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()]);
        await SeedAsync(rig);

        string[] Starts(JsonElement body) => [.. Points(body).Select(p => p.GetProperty("bucket_start").GetDateTimeOffset().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))];
        var week = await ConsoleRig.Get(rig.Owner, "/v1/analytics?bucket=week");
        Assert.Equal(["2026-09-14", "2026-10-05", "2026-10-12"], Starts(week));
        Assert.Equal(3, Points(week)[^1].GetProperty("tasks").GetInt32());
        var month = await ConsoleRig.Get(rig.Owner, "/v1/analytics?bucket=month");
        Assert.Equal(["2026-09-01", "2026-10-01"], Starts(month));
        Assert.Equal(4, Points(month)[^1].GetProperty("tasks").GetInt32());
    }

    [Fact]
    public async Task Tasks_group_by_account_repository_or_size()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()]);
        await SeedAsync(rig);

        string[] Groups(JsonElement body) => [.. Points(body).Select(p => $"{p.GetProperty("bucket_start").GetDateTimeOffset():MM-dd}:{p.GetProperty("group").GetString()}")];
        Assert.Equal(["09-20:owner", "10-08:alice", "10-14:alice", "10-15:owner"], Groups(await ConsoleRig.Get(rig.Owner, "/v1/analytics?group_by=account")));
        Assert.Equal(["09-20:o/r", "10-08:o/x", "10-14:o/x", "10-15:o/r"], Groups(await ConsoleRig.Get(rig.Owner, "/v1/analytics?group_by=repository")));
        Assert.Equal(["09-20:M", "10-08:L", "10-14:S", "10-15:M"], Groups(await ConsoleRig.Get(rig.Owner, "/v1/analytics?group_by=size")));
        var week = await ConsoleRig.Get(rig.Owner, "/v1/analytics?group_by=account&bucket=month");
        Assert.Equal(["09-01:owner", "10-01:alice", "10-01:owner"], Groups(week));
    }

    [Fact]
    public async Task The_governor_false_stop_proxy_and_continuations_are_summed_over_the_period()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()]);
        await SeedAsync(rig);

        var tasks = (await ConsoleRig.Get(rig.Owner, "/v1/analytics")).GetProperty("tasks");

        var modes = tasks.GetProperty("governor").GetProperty("modes").EnumerateArray().ToDictionary(m => m.GetProperty("mode").GetString()!);
        Assert.Equal(["enforce", "observe"], modes.Keys.Order());
        Assert.Equal((1, 1, 0, 0.0), (modes["enforce"].GetProperty("tasks").GetInt32(), modes["enforce"].GetProperty("checkpointed").GetInt32(), modes["enforce"].GetProperty("checkpointed_completed").GetInt32(),
            modes["enforce"].GetProperty("false_stop_rate").GetDouble()));
        Assert.Equal((4, 1, 1, 1.0), (modes["observe"].GetProperty("tasks").GetInt32(), modes["observe"].GetProperty("checkpointed").GetInt32(), modes["observe"].GetProperty("checkpointed_completed").GetInt32(),
            modes["observe"].GetProperty("false_stop_rate").GetDouble()));
        var decision = Assert.Single(tasks.GetProperty("governor").GetProperty("decisions").EnumerateArray());
        Assert.Equal(("spend", "checkpoint", 2, 2), (decision.GetProperty("signal").GetString(), decision.GetProperty("action").GetString(), decision.GetProperty("count").GetInt32(), decision.GetProperty("acted").GetInt32()));
        var continuations = tasks.GetProperty("continuations");
        Assert.Equal((1, 2, 1.0, 0.25), (continuations.GetProperty("tasks_with_continuation").GetInt32(), continuations.GetProperty("continuations").GetInt32(), continuations.GetProperty("completion_rate").GetDouble(),
            continuations.GetProperty("completion_rate_without").GetDouble()));
    }

    [Fact]
    public async Task Runs_over_time_count_finished_top_level_runs_and_leave_out_a_sessions_calls()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()]);
        async Task Run(string id, string? actor, ResultStatus status, decimal? usd, DateTimeOffset at, string? parent = null)
        {
            var start = ConsoleSeed.Start(id, Runs.DraftRequest(), actor, at.AddMinutes(-1), parent);
            await rig.Log.AppendAsync(start, default);
            await rig.Log.AppendAsync(ConsoleSeed.Finish(start, ConsoleSeed.Result(id, status, "s", usage: new Usage(100, 50, 0, 0, usd), actor: actor), at), default);
        }
        await Run("r-owner", null, ResultStatus.Completed, 0.2m, Now.AddHours(-1));
        await Run("r-alice-1", "alice", ResultStatus.Failed, 0.3m, Now.AddHours(-2));
        await Run("r-alice-2", "alice", ResultStatus.Completed, null, Now.AddDays(-1));
        await Run("r-call", "alice", ResultStatus.Completed, 9m, Now.AddHours(-1), parent: "r-owner");
        await Run("r-old", null, ResultStatus.Completed, 9m, Now.AddDays(-40));

        var all = (await ConsoleRig.Get(rig.Owner, "/v1/analytics?group_by=account")).GetProperty("runs").EnumerateArray().ToList();

        Assert.Equal([("10-14", "alice", 1), ("10-15", "alice", 1), ("10-15", "owner", 1)], all.Select(r => (r.GetProperty("bucket_start").GetDateTimeOffset().ToString("MM-dd", CultureInfo.InvariantCulture), r.GetProperty("group").GetString()!, r.GetProperty("runs").GetInt32())));
        var aliceToday = all.Single(r => r.GetProperty("group").GetString() == "alice" && r.GetProperty("bucket_start").GetDateTimeOffset().Day == 15);
        Assert.Equal((0, 1, 0.3m, 150), (aliceToday.GetProperty("completed").GetInt32(), aliceToday.GetProperty("failed").GetInt32(), aliceToday.GetProperty("usd").GetDecimal(), aliceToday.GetProperty("tokens").GetInt32()));
        Assert.Equal(0, (await ConsoleRig.Get(rig.Owner, "/v1/analytics?since=0d")).GetProperty("runs").GetArrayLength());
    }

    [Fact]
    public async Task An_account_gets_its_own_figures_and_the_owner_may_narrow_to_an_account()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()]);
        await SeedAsync(rig);
        using var alice = rig.As("alice");

        var own = await ConsoleRig.Get(alice, "/v1/analytics");
        Assert.Equal(2, own.GetProperty("tasks").GetProperty("total").GetProperty("tasks").GetInt32());
        Assert.Equal("alice", own.GetProperty("account").GetString());
        // Asking for the owner's figures changes nothing for an account.
        Assert.Equal(2, (await ConsoleRig.Get(alice, "/v1/analytics?account=owner")).GetProperty("tasks").GetProperty("total").GetProperty("tasks").GetInt32());

        Assert.Equal(5, (await ConsoleRig.Get(rig.Owner, "/v1/analytics")).GetProperty("tasks").GetProperty("total").GetProperty("tasks").GetInt32());
        Assert.Equal(2, (await ConsoleRig.Get(rig.Owner, "/v1/analytics?account=alice")).GetProperty("tasks").GetProperty("total").GetProperty("tasks").GetInt32());
        Assert.Equal(3, (await ConsoleRig.Get(rig.Owner, "/v1/analytics?account=owner")).GetProperty("tasks").GetProperty("total").GetProperty("tasks").GetInt32());
    }

    [Fact]
    public async Task The_period_defaults_to_30_days_and_bad_values_are_400()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b1", "t1", at: Now.AddDays(-31)));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b2", "t1", at: Now.AddDays(-29)));

        Assert.Equal(1, (await ConsoleRig.Get(rig.Owner, "/v1/analytics")).GetProperty("tasks").GetProperty("total").GetProperty("tasks").GetInt32());
        Assert.Equal(2, (await ConsoleRig.Get(rig.Owner, "/v1/analytics?since=90d")).GetProperty("tasks").GetProperty("total").GetProperty("tasks").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, await ConsoleRig.Status(rig.Owner, "/v1/analytics?bucket=hour"));
        Assert.Equal(HttpStatusCode.BadRequest, await ConsoleRig.Status(rig.Owner, "/v1/analytics?group_by=color"));
        Assert.Equal(HttpStatusCode.BadRequest, await ConsoleRig.Status(rig.Owner, "/v1/analytics?since=never"));
        var empty = await ConsoleRig.Get(rig.Owner, "/v1/analytics?since=1d");
        Assert.Equal(0, empty.GetProperty("tasks").GetProperty("total").GetProperty("tasks").GetInt32());
        Assert.False(empty.GetProperty("tasks").GetProperty("total").TryGetProperty("pass_rate", out _));
    }

    [Fact]
    public void Weeks_start_on_monday_and_months_on_the_first_in_utc()
    {
        var sunday = new DateTimeOffset(2026, 9, 20, 23, 59, 0, TimeSpan.FromHours(-5));
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero), LearningSeries.BucketStart(sunday, "day"));
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero), LearningSeries.BucketStart(sunday, "week"));
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), LearningSeries.BucketStart(sunday.AddDays(-5), "month"));
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), LearningSeries.BucketStart(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), "week"));
    }

    [Fact]
    public void An_empty_period_gives_zero_totals_and_no_rates()
    {
        var report = LearningSeries.Build([], null);

        Assert.Equal(0, report.Tasks);
        Assert.Empty(report.Points);
        Assert.Null(report.Total.PassRate);
        Assert.Empty(report.Governor.Decisions);
        Assert.Null(report.Continuations.CompletionRate);
        Assert.Null(report.Continuations.CompletionRateWithout);
    }
}
