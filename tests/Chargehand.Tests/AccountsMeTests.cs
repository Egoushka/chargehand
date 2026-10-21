using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>The routes the operator console reads for who is signed in and for the run list (ADR 0054, 0056).</summary>
public class AccountsMeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static ManualTimeProvider ClockAtNow()
    {
        var clock = new ManualTimeProvider();
        clock.Advance(Now - clock.GetUtcNow());
        return clock;
    }

    private static HttpClient Plain(TestServer s, bool follow = true) =>
        new(new HttpClientHandler { AllowAutoRedirect = follow }) { BaseAddress = s.BaseAddress };

    private static async Task<JsonElement> Body(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task Seed(JsonlRunLog log, string runId, string actor, DateTimeOffset finished, decimal usd, long tokens, RunRequest? request = null)
    {
        var req = request ?? Runs.DraftRequest();
        await log.AppendAsync(new StartRecord(runId, finished.AddMinutes(-1), new string('0', 32), req, 1, null, "earlier-boot", actor), CancellationToken.None);
        var result = Runs.Completed() with { TaskId = runId, Usage = new Usage(tokens, 0, 0, 0, usd), Actor = actor };
        await log.AppendAsync(new RunRecord(runId, finished.AddMinutes(-1), finished, req.Context.Preset, null, null, result, Actor: actor), CancellationToken.None);
    }

    [Fact]
    public async Task Accounts_me_says_who_the_key_is_what_it_may_do_and_what_it_has_spent()
    {
        var clock = ClockAtNow();
        AccountSettings alice = new("alice", MaxUsdPerDay: 5m, MaxUsdPerMonth: 60m, MaxTokensPerDay: 2000, MaxConcurrentRuns: 2, Repositories: ["/srv/app"], AllowServerCredential: true);
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [alice, new("bob", AllowServerCredential: true)], clock: clock);
        await Seed(s.Log, "a-today", "alice", Now.AddHours(-1), 1.25m, 300);
        await Seed(s.Log, "a-earlier-this-month", "alice", new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero), 2m, 100);
        await Seed(s.Log, "a-last-month", "alice", new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero), 40m, 9000);
        await Seed(s.Log, "b-today", "bob", Now.AddHours(-1), 3m, 700);
        using var anonymous = Plain(s);
        using var client = s.As(s.Keys.Create("alice", null).Key);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/accounts/me")).StatusCode);
        var me = await Body(await client.GetAsync("/v1/accounts/me"));

        Assert.Equal("alice", me.GetProperty("name").GetString());
        Assert.False(me.GetProperty("owner").GetBoolean());
        Assert.False(me.GetProperty("driven").GetBoolean());
        Assert.Equal("/srv/app", me.GetProperty("repositories")[0].GetString());
        var limits = me.GetProperty("limits");
        Assert.Equal((5m, 60m, 2000L, 2), (limits.GetProperty("max_usd_per_day").GetDecimal(), limits.GetProperty("max_usd_per_month").GetDecimal(),
            limits.GetProperty("max_tokens_per_day").GetInt64(), limits.GetProperty("max_concurrent_runs").GetInt32()));
        Assert.False(limits.TryGetProperty("max_tokens_per_month", out _));
        // Bob's run is not in alice's spend; last month's is not in this month's.
        var spend = me.GetProperty("spend");
        Assert.Equal((1.25m, 300L), (spend.GetProperty("day").GetProperty("usd").GetDecimal(), spend.GetProperty("day").GetProperty("tokens").GetInt64()));
        Assert.Equal((3.25m, 400L), (spend.GetProperty("month").GetProperty("usd").GetDecimal(), spend.GetProperty("month").GetProperty("tokens").GetInt64()));
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero), spend.GetProperty("day").GetProperty("resets_at").GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), spend.GetProperty("month").GetProperty("resets_at").GetDateTimeOffset());
        Assert.Equal(0, me.GetProperty("unfinished_runs").GetInt32());
        Assert.Contains("draft", me.GetProperty("presets").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task Accounts_me_of_the_owner_has_no_caps_and_shows_the_halt_state_and_unfinished_runs()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Hold = new() };
        await using var s = await TestServer.StartAsync(runtime, accounts: [new AccountSettings("alice", MaxConcurrentRuns: 3, AllowServerCredential: true)]);
        using var alice = s.As(s.Keys.Create("alice", null).Key);

        var owner = await Body(await s.Http.GetAsync("/v1/accounts/me"));
        Assert.Equal(("owner", true, true), (owner.GetProperty("name").GetString(), owner.GetProperty("owner").GetBoolean(), owner.GetProperty("driven").GetBoolean()));
        Assert.Empty(owner.GetProperty("limits").EnumerateObject());
        Assert.False(owner.GetProperty("halted").GetBoolean());

        var post = new HttpRequestMessage(HttpMethod.Post, "/v1/runs") { Content = JsonContent.Create(Runs.DraftRequest(), options: ContractJson.Options) };
        post.Headers.Add("Prefer", "wait=0");
        Assert.Equal(HttpStatusCode.Accepted, (await alice.SendAsync(post)).StatusCode);
        Assert.Equal(1, (await Body(await alice.GetAsync("/v1/accounts/me"))).GetProperty("unfinished_runs").GetInt32());
        Assert.Equal(0, (await Body(await s.Http.GetAsync("/v1/accounts/me"))).GetProperty("unfinished_runs").GetInt32());

        await s.Http.PostAsync("/v1/halt", null);
        Assert.True((await Body(await s.Http.GetAsync("/v1/accounts/me"))).GetProperty("halted").GetBoolean());
        runtime.Hold.TrySetResult();
    }

    [Fact]
    public async Task Accounts_me_is_not_open_to_a_run_token()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        var token = s.Tokens.Issue(new RunTokenClaims("run-1", "/srv/app", "abc1234", 1m, 1000, DateTimeOffset.UtcNow.AddMinutes(5), null), DateTimeOffset.UtcNow);
        using var session = s.As(token);

        Assert.Equal(HttpStatusCode.Forbidden, (await session.GetAsync("/v1/accounts/me")).StatusCode);
    }

    [Fact]
    public async Task The_run_list_filters_by_kind_repository_and_run_and_rows_say_what_the_ui_shows()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [new AccountSettings("alice", AllowServerCredential: true), new AccountSettings("bob", AllowServerCredential: true)]);
        var single = Runs.DraftRequest() with { Context = Runs.DraftRequest().Context with { Repository = new RepositoryRef("/srv/app", "abc1234def") } };
        var batch = Runs.DraftRequest() with { Driven = new RequestDriven([new DrivenTask("t1", Goal: "do it")]) };
        await Seed(s.Log, "r-single", "alice", Now, 0.5m, 1234, single);
        await Seed(s.Log, "r-batch", "alice", Now.AddMinutes(1), 1m, 10, batch);
        await Seed(s.Log, "r-bob", "bob", Now.AddMinutes(2), 1m, 10);
        using var alice = s.As(s.Keys.Create("alice", null).Key);

        async Task Expect(HttpClient http, string query, params string[] ids) =>
            Assert.Equal(ids, (await Body(await http.GetAsync($"/v1/runs{query}"))).GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("run_id").GetString()!).ToArray());

        await Expect(alice, "?kind=batch", "r-batch");
        await Expect(alice, "?kind=single", "r-single");
        await Expect(alice, "?repository=/srv/app", "r-single");
        await Expect(alice, "?run_id=r-batch", "r-batch");
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync("/v1/runs?kind=both")).StatusCode);
        // An account sees its own runs whatever actor it asks for; the owner can pick one.
        await Expect(alice, "?actor=bob", "r-batch", "r-single");
        await Expect(s.Http, "?actor=bob", "r-bob");
        await Expect(alice, "?run_id=r-bob");

        var row = (await Body(await alice.GetAsync("/v1/runs?run_id=r-single"))).GetProperty("runs")[0];
        Assert.Equal(("single", "/srv/app", "abc1234def", 1234L), (row.GetProperty("kind").GetString(), row.GetProperty("repository").GetString(), row.GetProperty("commit").GetString(), row.GetProperty("tokens").GetInt64()));
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.RunSummary, row));
    }
}
