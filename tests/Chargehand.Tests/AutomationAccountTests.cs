using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>ADR 0076: automated starts charged to automation.account are admitted as it, and held while its month forecast passes its cap when it asks for that.</summary>
public class AutomationAccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static ManualTimeProvider Clock()
    {
        var clock = new ManualTimeProvider();
        clock.Advance(Now - clock.GetUtcNow());
        return clock;
    }

    private static AccountSettings Bot(bool pause = true, decimal? perDay = null) =>
        new("bot", MaxUsdPerDay: perDay, MaxUsdPerMonth: 40m, Driven: true, AllowServerCredential: true, PauseOnForecast: pause);

    [Fact]
    public async Task A_forecast_over_the_cap_holds_an_automated_start_of_an_account_that_opted_in()
    {
        var bot = Bot();
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [bot], clock: Clock());
        // 14 in the last 7 days and 16 this month: 2 a day, 49 by the month's end, over 40.
        await SpendForecastTests.Seed(s.Log, "b-recent", "bot", Now.AddDays(-2), 14m);
        await SpendForecastTests.Seed(s.Log, "b-early", "bot", new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero), 2m);

        var start = await new RunServiceStarter(s.Service, bot).StartAsync(Runs.DraftRequest(), default);

        Assert.Null(start.RunId);
        Assert.Contains("paused", start.Refusal, StringComparison.Ordinal);
        Assert.Contains("$49.00", start.Refusal, StringComparison.Ordinal);
        Assert.Equal(0, s.Service.Unfinished("bot"));
    }

    [Fact]
    public async Task Without_the_opt_in_the_start_runs_as_the_account_and_a_person_is_never_held_by_the_forecast()
    {
        var bot = Bot(pause: false);
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [bot], clock: Clock());
        await SpendForecastTests.Seed(s.Log, "b-recent", "bot", Now.AddDays(-2), 14m);

        var start = await new RunServiceStarter(s.Service, bot).StartAsync(Runs.DraftRequest(), default);

        Assert.NotNull(start.RunId);
        Assert.Equal("bot", s.Service.Held.Single(r => r.Id == start.RunId).Actor);
    }

    [Fact]
    public async Task The_accounts_own_caps_apply_to_an_automated_start()
    {
        var bot = Bot(pause: false, perDay: 1m);
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [bot], clock: Clock());
        await SpendForecastTests.Seed(s.Log, "b-today", "bot", Now.AddHours(-1), 2m);

        var start = await new RunServiceStarter(s.Service, bot).StartAsync(Runs.DraftRequest(), default);

        Assert.Null(start.RunId);
        Assert.StartsWith("account 'bot': ", start.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_an_account_an_automated_start_runs_as_the_owner_as_before()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        var start = await new RunServiceStarter(s.Service).StartAsync(Runs.DraftRequest(), default);
        Assert.Equal(AccountSettings.Owner, s.Service.Held.Single(r => r.Id == start.RunId).Actor);
    }

    [Fact]
    public async Task Without_an_account_a_paused_server_credential_refuses_an_automated_start_for_intake_to_retry()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        await s.Service.ReportCredentialAsync(CredentialHealth.Server, "claude -p exited 1: Failed to authenticate. API Error: 401 OAuth access token has been revoked.", CancellationToken.None);

        var start = await new RunServiceStarter(s.Service).StartAsync(Runs.DraftRequest(), default);

        Assert.Null(start.RunId);
        Assert.Contains("credential", start.Refusal, StringComparison.Ordinal);
        Assert.Contains("revoked", start.Refusal, StringComparison.Ordinal);
        Assert.Empty(s.Service.Held);
    }

    [Theory]
    [InlineData("""{"name":"bot","driven":true,"allow_server_credential":true}""", "ghost", "automation.account: 'ghost'")]
    [InlineData("""{"name":"bot","allow_server_credential":true}""", "bot", "with driven: true")]
    [InlineData("""{"name":"bot","driven":true,"allow_server_credential":true,"pause_on_forecast":true}""", "bot", "pause_on_forecast needs max_usd_per_month")]
    public void The_profile_refuses_an_account_that_cannot_take_automated_starts(string account, string charged, string problem)
    {
        using var dir = new TempDir();
        var path = dir.Write("local.json", $$$"""
            {"schema":"profile/v1","worker_root":"/var/tmp/ch","repository_roots":["/srv/repos"],
             "http":{"api_key_secret":"k","accounts":[{{{account}}}]},
             "driven":{"enabled":true},
             "automation":{"account":"{{{charged}}}"}}
            """);
        var e = Assert.ThrowsAny<Exception>(() => Profile.Load(path));
        Assert.Contains(problem, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_accounts_list_says_which_account_carries_automated_starts_and_whether_it_pauses()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Bot(), new("alice", Driven: true, AllowServerCredential: true)],
            profile: root => Runs.Profile(root) with { Automation = new Chargehand.Modules.Intake.Api.AutomationSettings(Account: "bot") });

        var accounts = (await ConsoleRig.Get(rig.Owner, "/v1/accounts")).GetProperty("accounts").EnumerateArray()
            .ToDictionary(a => a.GetProperty("name").GetString()!, a => (a.GetProperty("automation").GetBoolean(), a.GetProperty("pause_on_forecast").GetBoolean()));
        Assert.Equal((true, true), accounts["bot"]);
        Assert.Equal((false, false), accounts["alice"]);
        Assert.Equal((false, false), accounts[AccountSettings.Owner]);
    }
}
