using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Automation;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>ADR 0100: the dollar cap binds API-billed runs only; a percent cap per usage window binds a subscription.</summary>
public class TwoDailyCapsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WeekEnd = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);

    private static ManualTimeProvider ClockAtNow()
    {
        var clock = new ManualTimeProvider();
        clock.Advance(Now - clock.GetUtcNow());
        return clock;
    }

    private static AccountSettings Account(decimal? usd = null, int? concurrent = null, decimal? pct7d = null, decimal? pct5h = null) =>
        new("alice", usd, MaxConcurrentRuns: concurrent, AllowServerCredential: true, MaxPctPerDay7d: pct7d, MaxPctPerDay5h: pct5h);

    private static RunRequest Budget5() => Runs.DraftRequest() with { Context = Runs.DraftRequest().Context with { BudgetUsd = 5m } };

    private static async Task<JsonElement> Body(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<HttpResponseMessage> Post(HttpClient http, RunRequest request)
    {
        var post = new HttpRequestMessage(HttpMethod.Post, "/v1/runs") { Content = JsonContent.Create(request, options: ContractJson.Options) };
        post.Headers.Add("Prefer", "wait=0");
        return await http.SendAsync(post);
    }

    private static async Task<List<HttpResponseMessage>> Starts(HttpClient http, int count)
    {
        List<HttpResponseMessage> all = [];
        for (var i = 0; i < count; i++)
            all.Add(await Post(http, Budget5()));
        return all;
    }

    /// <summary>One call of one run of alice's, stamped with the windows it saw.</summary>
    private static async Task Reading(JsonlRunLog log, string runId, DateTimeOffset at, double? seven, double? five, DateTimeOffset? sevenResets = null, DateTimeOffset? fiveResets = null)
    {
        await log.AppendAsync(new StartRecord(runId, at, new string('0', 32), Runs.DraftRequest(), 1, null, "earlier-boot", "alice"), CancellationToken.None);
        var usage = SubscriptionUsage.Of(five is null ? null : new UsageWindow(five.Value, fiveResets ?? at.AddHours(2)), seven is null ? null : new UsageWindow(seven.Value, sevenResets ?? WeekEnd))!;
        var chain = new PromptChain([], new AsSent("v", "a", "m", "d"));
        await log.AppendAsync(new CallRecord(runId, "n1", "worker", null, null, "p/small", at, 1, new TokenCounts(1, 1, 0, 0, 0), 0m, chain, Credential: CredentialKind.Subscription, SubscriptionUsage: usage), CancellationToken.None);
    }

    private static ScriptedRuntime Subscription() => new(Runs.DraftReply) { Hold = new(), Credential = CredentialKind.Subscription, UsageProvider = "claude" };

    [Fact]
    public async Task A_subscription_routed_account_admits_up_to_max_concurrent_runs_under_the_dollar_cap()
    {
        await using var s = await TestServer.StartAsync(Subscription(), accounts: [Account(usd: 20m, concurrent: 8)], clock: ClockAtNow());
        using var alice = s.As(s.Keys.Create("alice", null).Key);

        var started = await Starts(alice, 9);

        Assert.All(started.Take(8), r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        var refused = await Body(started[8]);
        Assert.Equal(HttpStatusCode.TooManyRequests, started[8].StatusCode);
        Assert.Equal("concurrency_exceeded", refused.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0m, (await Body(await alice.GetAsync("/v1/accounts/me"))).GetProperty("reserved_usd").GetDecimal());
    }

    [Fact]
    public async Task An_api_key_account_still_stops_at_the_dollar_cap_and_the_refusal_separates_reserved_from_spent()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply) { Hold = new() }, accounts: [Account(usd: 20m, concurrent: 8)], clock: ClockAtNow());
        using var alice = s.As(s.Keys.Create("alice", null).Key);

        var started = await Starts(alice, 5);

        Assert.All(started.Take(4), r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.Equal(HttpStatusCode.TooManyRequests, started[4].StatusCode);
        var error = (await Body(started[4])).GetProperty("error");
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains("$0.00 spent", message, StringComparison.Ordinal);
        Assert.Contains("$20.00 reserved by unfinished runs", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_seven_day_cap_refuses_once_the_days_share_is_used_and_the_text_names_the_amount()
    {
        await using var s = await TestServer.StartAsync(Subscription(), accounts: [Account(pct7d: 10m)], clock: ClockAtNow());
        using var alice = s.As(s.Keys.Create("alice", null).Key);
        await Reading(s.Log, "r1", Now.AddHours(-3), 0.30, null);
        await Reading(s.Log, "r2", Now.AddHours(-1), 0.41, null);

        var refused = await Post(alice, Budget5());

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        var error = (await Body(refused)).GetProperty("error");
        Assert.Equal("subscription_cap_exceeded", error.GetProperty("code").GetString());
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains("10% of the Claude seven-day window", message, StringComparison.Ordinal);
        Assert.Contains("11 points used", message, StringComparison.Ordinal);
        var standing = (await Body(await alice.GetAsync("/v1/accounts/me"))).GetProperty("subscription");
        Assert.Equal("measured", standing.GetProperty("state").GetString());
        Assert.Equal(11m, standing.GetProperty("seven_day").GetProperty("used_pct").GetDecimal());
        Assert.Equal(10m, standing.GetProperty("seven_day").GetProperty("cap_pct").GetDecimal());
        Assert.Equal("uncapped", standing.GetProperty("five_hour").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Below_the_seven_day_cap_a_start_is_admitted()
    {
        await using var s = await TestServer.StartAsync(Subscription(), accounts: [Account(pct7d: 12m)], clock: ClockAtNow());
        using var alice = s.As(s.Keys.Create("alice", null).Key);
        await Reading(s.Log, "r1", Now.AddHours(-3), 0.30, null);
        await Reading(s.Log, "r2", Now.AddHours(-1), 0.41, null);

        Assert.Equal(HttpStatusCode.Accepted, (await Post(alice, Budget5())).StatusCode);
    }

    [Fact]
    public async Task With_both_caps_the_window_that_is_used_up_refuses_and_names_itself()
    {
        await using var s = await TestServer.StartAsync(Subscription(), accounts: [Account(pct7d: 50m, pct5h: 5m)], clock: ClockAtNow());
        using var alice = s.As(s.Keys.Create("alice", null).Key);
        await Reading(s.Log, "r1", Now.AddHours(-2), 0.30, 0.10);
        await Reading(s.Log, "r2", Now.AddHours(-1), 0.35, 0.20);

        var error = (await Body(await Post(alice, Budget5()))).GetProperty("error");

        Assert.Equal("subscription_cap_exceeded", error.GetProperty("code").GetString());
        Assert.Contains("five-hour window", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_only_the_seven_day_cap_a_used_up_five_hour_window_never_refuses()
    {
        await using var s = await TestServer.StartAsync(Subscription(), accounts: [Account(pct7d: 10m)], clock: ClockAtNow());
        using var alice = s.As(s.Keys.Create("alice", null).Key);
        await Reading(s.Log, "r1", Now.AddHours(-2), 0.30, 0.10);
        await Reading(s.Log, "r2", Now.AddHours(-1), 0.32, 0.95);

        Assert.Equal(HttpStatusCode.Accepted, (await Post(alice, Budget5())).StatusCode);
    }

    [Fact]
    public async Task With_neither_cap_a_subscription_is_never_refused_and_the_standing_has_no_subscription_block()
    {
        await using var s = await TestServer.StartAsync(Subscription(), accounts: [Account()], clock: ClockAtNow());
        using var alice = s.As(s.Keys.Create("alice", null).Key);
        await Reading(s.Log, "r1", Now.AddHours(-2), 0.10, 0.10);
        await Reading(s.Log, "r2", Now.AddHours(-1), 0.90, 0.90);

        Assert.Equal(HttpStatusCode.Accepted, (await Post(alice, Budget5())).StatusCode);
        Assert.False((await Body(await alice.GetAsync("/v1/accounts/me"))).TryGetProperty("subscription", out var block) && block.ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task A_cap_with_no_reading_today_admits_and_says_so()
    {
        await using var s = await TestServer.StartAsync(Subscription(), accounts: [Account(pct7d: 10m)], clock: ClockAtNow());
        using var alice = s.As(s.Keys.Create("alice", null).Key);
        await Reading(s.Log, "old", Now.AddDays(-1), 0.90, null);

        Assert.Equal(HttpStatusCode.Accepted, (await Post(alice, Budget5())).StatusCode);
        var standing = (await Body(await alice.GetAsync("/v1/accounts/me"))).GetProperty("subscription");
        Assert.Equal("no_reading", standing.GetProperty("seven_day").GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_cap_on_a_runtime_that_reports_no_utilization_is_not_measurable_and_refuses()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Credential = CredentialKind.Subscription };
        await using var s = await TestServer.StartAsync(runtime, accounts: [Account(pct7d: 10m)], clock: ClockAtNow());
        using var alice = s.As(s.Keys.Create("alice", null).Key);

        var refused = await Post(alice, Budget5());

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("subscription_not_measurable", (await Body(refused)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("not_measurable", (await Body(await alice.GetAsync("/v1/accounts/me"))).GetProperty("subscription").GetProperty("state").GetString());
    }

    [Fact]
    public void A_restarted_window_adds_its_own_use_and_the_days_readings_only_count()
    {
        var first = new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
        var second = first.AddDays(7);
        SubscriptionReading At(int hour, double used, DateTimeOffset resets) => new(Now.AddHours(hour), new SubscriptionUsage(null, new UsageWindow(used, resets)));

        var used = SubscriptionCap.UsedPoints([At(-6, 0.50, first), At(-5, 0.60, first), At(-2, 0.05, second), At(-1, 0.10, second)], u => u.SevenDay);

        Assert.Equal(20m, used);
        Assert.Null(SubscriptionCap.UsedPoints([], u => u.SevenDay));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void A_percent_cap_outside_zero_to_one_hundred_is_a_profile_problem(int pct)
    {
        Assert.Contains("max_pct_per_day_7d must be above 0", Assert.Single(AccountSettings.Problems([Account(pct7d: pct)])), StringComparison.Ordinal);
        Assert.Contains("max_pct_per_day_5h must be above 0", Assert.Single(AccountSettings.Problems([Account(pct5h: pct)])), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_trigger_waiting_on_a_cap_counts_as_queued()
    {
        using var rig = new IntakeRig(new AutomationGitHub(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, [IntakeRig.Author]));
        rig.Starter.Refuse = "account 'bot': the daily dollar cap of $20.00 is used up";
        rig.Labeled(7);
        await rig.Poll();
        var settings = new ServerSettings(0, "k", Repo.Path("presets"), IntakeStatePath: rig.StateFile);

        var source = Assert.Single(ConsoleSources.Build(settings, rig.Intake).Sources);

        Assert.Equal(1, source.Queued);
    }
}
