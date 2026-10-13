using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;
using Chargehand.Runtime;
using Chargehand.Server;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Chargehand.Tests;

/// <summary>Accounts and named keys (ADR 0050): which key is whom, what each sees, what each may spend, and the actor on every record.</summary>
public class AccountsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static AccountSettings Account(string name, decimal? usdDay = null, decimal? usdMonth = null, long? tokensDay = null, long? tokensMonth = null,
        int? concurrent = null, bool driven = false, string[]? repositories = null) =>
        new(name, usdDay, usdMonth, tokensDay, tokensMonth, concurrent, driven, repositories, AllowServerCredential: true);

    private static ManualTimeProvider ClockAtNow()
    {
        var clock = new ManualTimeProvider();
        clock.Advance(Now - clock.GetUtcNow());
        return clock;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<HttpResponseMessage> Post(HttpClient http, RunRequest request, int wait = 30)
    {
        var post = new HttpRequestMessage(HttpMethod.Post, "/v1/runs") { Content = JsonContent.Create(request, options: ContractJson.Options) };
        post.Headers.Add("Prefer", $"wait={wait}");
        return await http.SendAsync(post);
    }

    /// <summary>A finished run of <paramref name="actor"/>, as the log would hold it.</summary>
    private static async Task Seed(JsonlRunLog log, string runId, string? actor, DateTimeOffset finished, decimal usd, long tokens = 0, string? parent = null, RunRequest? request = null)
    {
        var req = request ?? Runs.DraftRequest();
        await log.AppendAsync(new StartRecord(runId, finished.AddMinutes(-1), new string('0', 32), req, 1, parent, "earlier-boot", actor), CancellationToken.None);
        var result = Runs.Completed() with { TaskId = runId, Usage = new Usage(tokens, 0, 0, 0, usd), Actor = actor };
        await log.AppendAsync(new RunRecord(runId, finished.AddMinutes(-1), finished, req.Context.Preset, null, null, result, Actor: actor), CancellationToken.None);
    }

    private static string Key(TestServer s, string account) => s.Keys.Create(account, null).Key;

    [Fact]
    public void A_key_is_kept_as_a_salted_hash_and_opens_its_account()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "accounts.json");
        var store = new AccountKeyStore(path);
        var (id, key) = store.Create("alice", "laptop");
        var second = store.Create("alice", null);

        var secret = key.Split('.')[2];
        Assert.Matches(@"^chk1\.[0-9a-f]{16}\.[A-Za-z0-9_-]{43}$", key);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.Contains(id, text, StringComparison.Ordinal);
        Assert.NotEqual(store.List()[0].Salt, store.List()[1].Salt);
        Assert.NotEqual(store.List()[0].Hash, store.List()[1].Hash);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

        Assert.Equal("alice", store.Authenticate(key)?.Account);
        Assert.Equal("alice", store.Authenticate(second.Key)?.Account);
        Assert.Null(store.Authenticate($"chk1.{id}.{new string('a', 43)}"));
        Assert.Null(store.Authenticate($"chk1.{new string('0', 16)}.{secret}"));
        Assert.Null(store.Authenticate(key.Replace("chk1.", "chk2.", StringComparison.Ordinal)));
        Assert.Null(store.Authenticate("not-a-key"));
    }

    [Fact]
    public void A_revoked_key_stops_working_for_a_store_that_already_read_the_file()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "accounts.json");
        var server = new AccountKeyStore(path);
        var key = server.Create("alice", null);
        Assert.NotNull(server.Authenticate(key.Key));

        Assert.True(new AccountKeyStore(path).Revoke(key.Id));

        Assert.Null(server.Authenticate(key.Key));
        Assert.False(new AccountKeyStore(path).Revoke(key.Id));
        Assert.False(new AccountKeyStore(path).Revoke("0000000000000000"));
    }

    [Fact]
    public async Task Every_key_maps_to_an_account_and_anything_else_is_401()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [Account("alice")]);
        var alice = s.Keys.Create("alice", null);
        var revoked = s.Keys.Create("alice", null);
        s.Keys.Revoke(revoked.Id);
        var ghost = Key(s, "ghost");

        Assert.Equal(HttpStatusCode.OK, (await s.As(alice.Key).GetAsync("/v1/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.As(revoked.Key).GetAsync("/v1/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.As(ghost).GetAsync("/v1/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.As($"chk1.{alice.Id}.{new string('a', 43)}").GetAsync("/v1/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.As("chk1.0000000000000000.x").GetAsync("/v1/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.As(alice.Key + "x").GetAsync("/v1/mcp")).StatusCode);
        // The key also opens /v1/mcp's gate: it is not a 401 there.
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await s.As(alice.Key).PostAsync("/v1/mcp", new StringContent("{}"))).StatusCode);
    }

    [Fact]
    public async Task The_single_key_is_the_owner_and_its_runs_carry_the_actor()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        var res = await Post(s.Http, Runs.DraftRequest());
        var body = await Body(res);
        var id = body.GetProperty("task_id").GetString()!;

        Assert.Equal("owner", body.GetProperty("actor").GetString());
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.Result, body));
        await ServerTests.WaitUntil(async () => (await s.Log.ReadAsync(id, CancellationToken.None)).Run is not null);
        var entry = await s.Log.ReadAsync(id, CancellationToken.None);
        Assert.Equal("owner", entry.Start!.Actor);
        Assert.Equal("owner", entry.Run!.Actor);
        var list = await Body(await s.Http.GetAsync("/v1/runs"));
        var row = Assert.Single(list.GetProperty("runs").EnumerateArray());
        Assert.Equal("owner", row.GetProperty("actor").GetString());
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.RunSummary, row));
    }

    [Fact]
    public async Task A_run_of_an_account_carries_its_name_on_the_record_the_result_and_the_events()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Hold = new() };
        await using var s = await TestServer.StartAsync(runtime, accounts: [Account("alice")]);
        using var alice = s.As(Key(s, "alice"));

        var queued = await Post(alice, Runs.DraftRequest(), wait: 0);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var status = await Body(queued);
        Assert.Equal("alice", status.GetProperty("actor").GetString());
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.RunStatus, status));
        var location = queued.Headers.Location!;
        runtime.Hold.SetResult();

        HttpResponseMessage done = null!;
        await ServerTests.WaitUntil(async () => (done = await alice.GetAsync(location)).StatusCode == HttpStatusCode.OK);
        var result = await Body(done);
        Assert.Equal("alice", result.GetProperty("actor").GetString());
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.Result, result));
        var id = result.GetProperty("task_id").GetString()!;
        var entry = await s.Log.ReadAsync(id, CancellationToken.None);
        Assert.Equal(("alice", "alice"), (entry.Start!.Actor, entry.Run!.Actor));

        var events = await (await alice.GetAsync($"{location}/events")).Content.ReadAsStringAsync();
        Assert.Contains("\"actor\":\"alice\"", events, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_account_lists_reads_and_cancels_only_its_own_runs_and_the_owner_sees_all()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply);
        await using var s = await TestServer.StartAsync(runtime, accounts: [Account("alice"), Account("bob")]);
        using var alice = s.As(Key(s, "alice"));
        using var bob = s.As(Key(s, "bob"));
        var a = (await Body(await Post(alice, Runs.DraftRequest()))).GetProperty("task_id").GetString()!;
        var b = (await Body(await Post(bob, Runs.DraftRequest()))).GetProperty("task_id").GetString()!;
        await Seed(s.Log, "run-before-accounts", null, Now, 0);
        await ServerTests.WaitUntil(async () => (await s.Log.ReadAsync(b, CancellationToken.None)).Run is not null && (await s.Log.ReadAsync(a, CancellationToken.None)).Run is not null);

        static IEnumerable<string> Ids(JsonElement list) => list.GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("run_id").GetString()!);
        Assert.Equal([a], Ids(await Body(await alice.GetAsync("/v1/runs"))));
        Assert.Equal([b], Ids(await Body(await bob.GetAsync("/v1/runs"))));
        Assert.Equal(new[] { a, b, "run-before-accounts" }.Order(), Ids(await Body(await s.Http.GetAsync("/v1/runs"))).Order());

        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/v1/runs/{a}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/v1/runs/{b}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/v1/runs/{b}/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/v1/runs/run-before-accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsync($"/v1/runs/{b}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Http.GetAsync($"/v1/runs/{b}")).StatusCode);
    }

    [Fact]
    public async Task Cancel_reaches_a_running_run_of_the_same_account_or_the_owner_and_nobody_else()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Hold = new() };
        await using var s = await TestServer.StartAsync(runtime, accounts: [Account("alice"), Account("bob")]);
        using var alice = s.As(Key(s, "alice"));
        using var bob = s.As(Key(s, "bob"));
        var first = (await Post(alice, Runs.DraftRequest(), wait: 0)).Headers.Location!;
        var second = (await Post(alice, Runs.DraftRequest(), wait: 0)).Headers.Location!;

        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"{first}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync(first)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await alice.GetAsync(first)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await alice.PostAsync($"{first}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await s.Http.PostAsync($"{second}/cancel", null)).StatusCode);
        foreach (var location in new[] { first, second })
        {
            HttpResponseMessage done = null!;
            await ServerTests.WaitUntil(async () => (done = await alice.GetAsync(location)).StatusCode == HttpStatusCode.OK);
            Assert.Equal("cancelled", (await Body(done)).GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Only_the_owner_halts_and_resumes()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [Account("alice")]);
        using var alice = s.As(Key(s, "alice"));

        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsync("/v1/halt", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Http.PostAsync("/v1/halt", null)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post(alice, Runs.DraftRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsync("/v1/resume", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Http.PostAsync("/v1/resume", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(alice, Runs.DraftRequest())).StatusCode);
    }

    [Fact]
    public async Task A_daily_dollar_cap_that_is_used_up_refuses_with_the_window_and_what_remains()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [Account("alice", usdDay: 1m), Account("bob", usdDay: 1m)], clock: ClockAtNow());
        using var alice = s.As(Key(s, "alice"));
        await Seed(s.Log, "run-bob", "bob", Now.AddHours(-1), 50m);
        Assert.Equal(HttpStatusCode.OK, (await Post(alice, Runs.DraftRequest())).StatusCode);

        await Seed(s.Log, "run-alice", "alice", Now.AddHours(-2), 1.5m);
        var res = await Post(alice, Runs.DraftRequest());

        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.Equal("43200", res.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var body = await Body(res);
        var error = body.GetProperty("error");
        Assert.Equal(("budget_exceeded", "day", "usd"), (error.GetProperty("code").GetString(), error.GetProperty("window").GetString(), error.GetProperty("unit").GetString()));
        Assert.Equal((1m, 0m), (error.GetProperty("limit").GetDecimal(), error.GetProperty("remaining").GetDecimal()));
        Assert.True(error.GetProperty("spent").GetDecimal() >= 1.5m);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero), error.GetProperty("resets_at").GetDateTimeOffset());
        Assert.Contains("daily dollar cap of $1.00", body.GetProperty("errors")[0].GetString(), StringComparison.Ordinal);
        // The owner has no caps, whatever it spent.
        await Seed(s.Log, "run-owner", "owner", Now.AddHours(-1), 500m);
        Assert.Equal(HttpStatusCode.OK, (await Post(s.Http, Runs.DraftRequest())).StatusCode);
    }

    [Fact]
    public async Task Yesterdays_spend_counts_against_the_month_and_last_months_against_neither()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [Account("alice", usdDay: 1m, usdMonth: 4m)], clock: ClockAtNow());
        using var alice = s.As(Key(s, "alice"));
        await Seed(s.Log, "run-september", "alice", new DateTimeOffset(2026, 9, 30, 23, 59, 0, TimeSpan.Zero), 100m);
        Assert.Equal(HttpStatusCode.OK, (await Post(alice, Runs.DraftRequest())).StatusCode);

        await Seed(s.Log, "run-yesterday", "alice", new DateTimeOffset(2026, 10, 14, 23, 0, 0, TimeSpan.Zero), 5m);
        var res = await Post(alice, Runs.DraftRequest());

        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        var error = (await Body(res)).GetProperty("error");
        Assert.Equal(("month", 4m), (error.GetProperty("window").GetString(), error.GetProperty("limit").GetDecimal()));
        Assert.True(error.GetProperty("spent").GetDecimal() >= 5m);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), error.GetProperty("resets_at").GetDateTimeOffset());
    }

    [Fact]
    public async Task A_token_cap_refuses_the_same_way()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [Account("alice", tokensDay: 1000)], clock: ClockAtNow());
        using var alice = s.As(Key(s, "alice"));
        await Seed(s.Log, "run-1", "alice", Now.AddHours(-3), 0m, tokens: 600);
        Assert.Equal(HttpStatusCode.OK, (await Post(alice, Runs.DraftRequest())).StatusCode);
        await Seed(s.Log, "run-2", "alice", Now.AddHours(-2), 0m, tokens: 500);

        var error = (await Body(await Post(alice, Runs.DraftRequest()))).GetProperty("error");

        Assert.Equal(("budget_exceeded", "day", "tokens"), (error.GetProperty("code").GetString(), error.GetProperty("window").GetString(), error.GetProperty("unit").GetString()));
        Assert.Equal(1000m, error.GetProperty("limit").GetDecimal());
        Assert.True(error.GetProperty("spent").GetDecimal() >= 1100m);
    }

    [Fact]
    public async Task A_run_is_started_with_its_budget_cut_to_what_the_window_has_left()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [Account("alice", usdDay: 2m)], clock: ClockAtNow());
        using var alice = s.As(Key(s, "alice"));
        await Seed(s.Log, "run-1", "alice", Now.AddHours(-1), 1.5m);
        var request = Runs.DraftRequest();

        var big = await Body(await Post(alice, request with { Context = request.Context with { BudgetUsd = 5m } }));
        var small = await Body(await Post(alice, request with { Context = request.Context with { BudgetUsd = 0.25m } }));

        Assert.Equal(0.5m, (await s.Log.ReadAsync(big.GetProperty("task_id").GetString()!, CancellationToken.None)).Start!.Request.Context.BudgetUsd);
        Assert.Equal(0.25m, (await s.Log.ReadAsync(small.GetProperty("task_id").GetString()!, CancellationToken.None)).Start!.Request.Context.BudgetUsd);
    }

    [Fact]
    public void The_windows_are_the_utc_day_and_calendar_month()
    {
        var late = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.FromHours(-5));
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), AccountPolicy.DayStart(late));
        Assert.Equal(new DateTimeOffset(2027, 1, 2, 0, 0, 0, TimeSpan.Zero), AccountPolicy.NextDay(late));
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), AccountPolicy.MonthStart(late));
        Assert.Equal(new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero), AccountPolicy.NextMonth(late));
    }

    [Fact]
    public void A_cap_is_met_when_the_spend_reaches_it_and_the_tightest_window_wins()
    {
        var account = Account("alice", usdDay: 10m, usdMonth: 30m);
        Assert.Null(AccountPolicy.Refusal(account, Runs.DraftRequest(), 0, new(9.99m, 0), new(29m, 0), Now));
        Assert.Equal("day", AccountPolicy.Refusal(account, Runs.DraftRequest(), 0, new(10m, 0), new(29m, 0), Now)!.Window);
        Assert.Equal("month", AccountPolicy.Refusal(account, Runs.DraftRequest(), 0, new(1m, 0), new(30m, 0), Now)!.Window);

        var request = Runs.DraftRequest() with { Context = Runs.DraftRequest().Context with { BudgetUsd = 100m } };
        Assert.Equal(1m, AccountPolicy.Clamp(account, request, new(9m, 0), new(10m, 0)).Context.BudgetUsd);
        Assert.Equal(9m, AccountPolicy.Clamp(account, request, new(1m, 0), new(10m, 0)).Context.BudgetUsd);
        Assert.Equal(2m, AccountPolicy.Clamp(account, request, new(1m, 0), new(28m, 0)).Context.BudgetUsd);
        Assert.Equal(100m, AccountPolicy.Clamp(Account("free"), request, new(9m, 0), new(10m, 0)).Context.BudgetUsd);
    }

    [Fact]
    public void A_batch_is_cut_to_what_is_left_in_dollars_and_tokens()
    {
        var account = Account("alice", usdDay: 10m, tokensMonth: 5000, driven: true);
        var batch = new RunRequest("request/v1", "x", new RequestContext(false, "driven", Repository: new RepositoryRef("/r", "abc1234")),
            Driven: new RequestDriven([new DrivenTask("t", Goal: "g")], MaxUsdTotal: 50m));

        var cut = AccountPolicy.Clamp(account, batch, new(4m, 0), new(0m, 1200)).Driven!;

        Assert.Equal((6m, 3800L), (cut.MaxUsdTotal, cut.MaxTokensTotal));
        Assert.Equal(2m, AccountPolicy.Clamp(account, batch with { Driven = batch.Driven! with { MaxUsdTotal = 2m } }, new(4m, 0), new(0m, 0)).Driven!.MaxUsdTotal);
    }

    [Fact]
    public async Task Unfinished_runs_count_against_the_concurrency_cap_per_account()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Hold = new() };
        await using var s = await TestServer.StartAsync(runtime, accounts: [Account("alice", concurrent: 1), Account("bob", concurrent: 1)]);
        using var alice = s.As(Key(s, "alice"));
        using var bob = s.As(Key(s, "bob"));

        Assert.Equal(HttpStatusCode.Accepted, (await Post(alice, Runs.DraftRequest(), wait: 0)).StatusCode);
        var refused = await Post(alice, Runs.DraftRequest(), wait: 0);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("concurrency_exceeded", (await Body(refused)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Accepted, (await Post(bob, Runs.DraftRequest(), wait: 0)).StatusCode);
    }

    [Fact]
    public async Task A_batch_needs_the_driven_right()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [Account("alice"), Account("bob", driven: true)]);
        var batch = new RunRequest("request/v1", "x", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/r", "abc1234")),
            Driven: new RequestDriven([new DrivenTask("t1", Goal: "g")]));

        var refused = await Post(s.As(Key(s, "alice")), batch);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("driven_not_allowed", (await Body(refused)).GetProperty("error").GetProperty("code").GetString());
        Assert.NotEqual(HttpStatusCode.Forbidden, (await Post(s.As(Key(s, "bob")), batch)).StatusCode);
    }

    [Theory]
    [InlineData("/srv/allowed", true)]
    [InlineData("/srv/allowed/project", true)]
    [InlineData("/srv/allowed/", true)]
    [InlineData("/srv/allowed-other", false)]
    [InlineData("/srv/allowed/../etc", false)]
    [InlineData("/srv", false)]
    public void An_account_with_repositories_may_name_only_those_and_below(string path, bool allowed)
    {
        var account = Account("alice", repositories: ["/srv/allowed"]);
        var request = Runs.DraftRequest() with { Context = Runs.DraftRequest().Context with { Repository = new RepositoryRef(path, "abc1234") } };
        var refusal = AccountPolicy.Refusal(account, request, 0, new(0, 0), new(0, 0), Now);
        Assert.Equal(allowed, refusal is null);
        Assert.Equal(allowed ? null : "repository_not_allowed", refusal?.Code);
    }

    [Fact]
    public async Task An_account_with_repositories_must_name_one()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [Account("alice", repositories: ["/srv/allowed"])]);
        using var alice = s.As(Key(s, "alice"));

        var none = await Post(alice, Runs.DraftRequest());
        var elsewhere = await Post(alice, Runs.DraftRequest() with { Context = Runs.DraftRequest().Context with { Repository = new RepositoryRef("/srv/other", "abc1234") } });

        Assert.Equal(HttpStatusCode.Forbidden, none.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, elsewhere.StatusCode);
        Assert.Equal("repository_not_allowed", (await Body(elsewhere)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_batch_cannot_continue_another_accounts_run()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [Account("alice", driven: true), Account("bob", driven: true)]);
        using var alice = s.As(Key(s, "alice"));
        await Seed(s.Log, "run-bob", "bob", Now, 0);
        await Seed(s.Log, "run-alice", "alice", Now, 0);
        RunRequest Continue(string run) => new("request/v1", "x", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/r", "abc1234")),
            Driven: new RequestDriven([new DrivenTask("t1", ContinueFrom: new ContinueFrom(run, "t1"))]));

        var refused = await Post(alice, Continue("run-bob"));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("continue_not_allowed", (await Body(refused)).GetProperty("error").GetProperty("code").GetString());
        Assert.NotEqual(HttpStatusCode.Forbidden, (await Post(alice, Continue("run-alice"))).StatusCode);
    }

    [Fact]
    public async Task A_session_token_carries_its_account_and_reads_only_that_accounts_runs()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.WorkerReply), accounts: [Account("alice"), Account("bob")]);
        var repo = Runs.GitRepo(s.WorkerRoot);
        await Seed(s.Log, "run-alice", "alice", Now, 0);
        await Seed(s.Log, "run-bob", "bob", Now, 0);
        var claims = new RunTokenClaims("run-task", repo.Path, repo.Commit, null, null, DateTimeOffset.UtcNow.AddHours(1), "alice");
        var token = s.Tokens.Issue(claims, DateTimeOffset.UtcNow);
        Assert.Equal("alice", s.Tokens.Validate(token, DateTimeOffset.UtcNow)!.Actor);
        using var session = s.As(token);

        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/v1/runs/run-alice")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await session.GetAsync("/v1/runs/run-bob")).StatusCode);
        var started = await Body(await Post(session, new RunRequest("request/v1", "What does the README say?", new RequestContext(false, "default", Repository: repo))));
        Assert.Equal("alice", started.GetProperty("actor").GetString());
        var entry = await s.Log.ReadAsync(started.GetProperty("task_id").GetString()!, CancellationToken.None);
        Assert.Equal(("alice", "run-task"), (entry.Start!.Actor, entry.Start.ParentRunId));
    }

    [Fact]
    public async Task A_token_from_before_accounts_is_the_owners()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.WorkerReply), accounts: [Account("alice")]);
        var repo = Runs.GitRepo(s.WorkerRoot);
        await Seed(s.Log, "run-before", null, Now, 0);
        var token = s.Tokens.Issue(new RunTokenClaims("run-task", repo.Path, repo.Commit, null, null, DateTimeOffset.UtcNow.AddHours(1)), DateTimeOffset.UtcNow);
        using var session = s.As(token);

        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/v1/runs/run-before")).StatusCode);
        var started = await Body(await Post(session, new RunRequest("request/v1", "What does the README say?", new RequestContext(false, "default", Repository: repo))));
        Assert.Equal("owner", started.GetProperty("actor").GetString());
    }

    [Fact]
    public async Task Over_MCP_an_account_runs_as_itself_and_meets_its_caps()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [Account("alice", usdDay: 1m)], clock: ClockAtNow());
        var key = Key(s, "alice");
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(s.BaseAddress, "/v1/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" },
        }));
        var arguments = JsonSerializer.SerializeToElement(Runs.DraftRequest(), ContractJson.Options).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());

        var ok = await client.CallToolAsync(OrchestrateTool.Name, arguments);
        Assert.True(ok.IsError != true);
        Assert.Equal("alice", ok.StructuredContent!.Value.GetProperty("actor").GetString());

        await Seed(s.Log, "run-spent", "alice", Now.AddHours(-1), 2m);
        var refused = await client.CallToolAsync(OrchestrateTool.Name, arguments);
        Assert.True(refused.IsError);
        Assert.Contains("budget_exceeded", ((TextContentBlock)refused.Content[0]).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_MCP_session_belongs_to_the_account_that_opened_it()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [Account("alice"), Account("bob")]);
        using var alice = s.As(Key(s, "alice"));
        using var bob = s.As(Key(s, "bob"));
        HttpRequestMessage Mcp(string json, string? session = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/mcp") { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            if (session is not null)
                request.Headers.Add("Mcp-Session-Id", session);
            return request;
        }
        var opened = await alice.SendAsync(Mcp("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}"""));
        var session = opened.Headers.GetValues("Mcp-Session-Id").Single();
        const string List = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";

        Assert.Equal(HttpStatusCode.OK, (await alice.SendAsync(Mcp(List, session))).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await bob.SendAsync(Mcp(List, session))).StatusCode);
    }

    [Fact]
    public async Task Spend_counts_finished_runs_by_their_usage_and_unfinished_ones_by_their_calls_and_a_batch_once()
    {
        using var dir = new TempDir();
        var log = new JsonlRunLog(System.IO.Path.Combine(dir.Path, "log.jsonl"));
        var batch = new RunRequest("request/v1", "x", new RequestContext(false, "driven"), Driven: new RequestDriven([new DrivenTask("t", Goal: "g")]));
        await Seed(log, "run-in", "alice", Now, 1.25m, tokens: 100);
        await Seed(log, "run-edge", "alice", Now.AddHours(-12), 0.75m, tokens: 40);
        await Seed(log, "run-old", "alice", Now.AddHours(-13), 9m, tokens: 9);
        await Seed(log, "run-bob", "bob", Now, 9m, tokens: 9);
        await Seed(log, "batch", "alice", Now, 4m, tokens: 1000, request: batch);
        await Seed(log, "batch-task", "alice", Now, 3m, tokens: 800, parent: "batch");
        await Seed(log, "batch-call", "alice", Now, 1m, tokens: 50, parent: "batch-task");
        // A resend with answers is a run of its own, not part of the one it resends.
        await Seed(log, "run-resend", "alice", Now, 0.5m, tokens: 10, parent: "run-in");
        await log.AppendAsync(new StartRecord("run-live", Now.AddHours(-1), new string('0', 32), Runs.DraftRequest(), Environment.ProcessId, BootId: ProcessBoot.Id, Actor: "alice"), CancellationToken.None);
        await log.AppendAsync(new CallRecord("run-live", "n1", "worker", null, null, "p/small", Now.AddMinutes(-30), 1, new TokenCounts(70, 30, 0, 0, 0), 0.2m, new PromptChain([], new AsSent("v", "a", "m", "d"))), CancellationToken.None);
        await log.AppendAsync(new CallRecord("run-live", "n0", "worker", null, null, "p/small", Now.AddHours(-13), 1, new TokenCounts(500, 0, 0, 0, 0), 5m, new PromptChain([], new AsSent("v", "a", "m", "d"))), CancellationToken.None);

        var spend = await log.SpendAsync("alice", Now.AddHours(-12), CancellationToken.None);

        Assert.Equal(1.25m + 0.75m + 4m + 0.5m + 0.2m, spend.Usd);
        Assert.Equal(100 + 40 + 1000 + 10 + 100, spend.Tokens);
    }

    [Fact]
    public void The_accounts_commands_add_list_and_revoke_without_ever_listing_a_key()
    {
        using var dir = new TempDir();
        var keys = System.IO.Path.Combine(dir.Path, "accounts.json");
        var profile = new Profile("profile/v1", Http: new HttpSettings("api", Accounts: [Account("alice", usdDay: 5m, driven: true)]));
        int Run(out string stdout, out string stderr, params string[] args)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = AccountsCli.Run(args, profile, keys, output, error);
            (stdout, stderr) = (output.ToString(), error.ToString());
            return code;
        }

        Assert.Equal(2, Run(out _, out var unknown, "add", "mallory"));
        Assert.Contains("alice", unknown, StringComparison.Ordinal);
        Assert.Equal(2, Run(out _, out _, "add", "owner"));
        Assert.Equal(0, Run(out var key, out var notice, "add", "alice", "--label", "laptop"));
        Assert.Matches(@"^chk1\.[0-9a-f]{16}\.[A-Za-z0-9_-]{43}\r?\n$", key);
        var id = key.Split('.')[1];
        Assert.Contains(id, notice, StringComparison.Ordinal);

        Assert.Equal(0, Run(out var list, out _, "list"));
        Assert.DoesNotContain(key.Trim().Split('.')[2], list, StringComparison.Ordinal);
        Assert.Contains($"{id}", list, StringComparison.Ordinal);
        Assert.Contains("laptop", list, StringComparison.Ordinal);
        Assert.Contains("usd/day 5", list, StringComparison.Ordinal);
        Assert.Contains("server's model key", list, StringComparison.Ordinal);
        Assert.Contains("active", list, StringComparison.Ordinal);

        Assert.Equal(0, Run(out _, out _, "revoke", id));
        Assert.Equal(1, Run(out _, out _, "revoke", id));
        Run(out var again, out _, "list");
        Assert.Contains("revoked", again, StringComparison.Ordinal);
        Assert.Equal(2, Run(out _, out _, "bogus"));
    }

    [Fact]
    public void The_profile_declares_accounts_and_refuses_ones_that_cannot_work()
    {
        using var dir = new TempDir();
        Profile Load(string accounts) => Profile.Load(dir.Write("p.json", """{"schema":"profile/v1","http":{"api_key_secret":"k","accounts":""" + accounts + "}}"));

        var http = Load("""[{"name":"alice","max_usd_per_day":5,"max_tokens_per_month":1000000,"max_concurrent_runs":2,"driven":true,"repositories":["/srv/a"]},{"name":"bob"}]""").Http!;
        Assert.Equal(("alice", 5m, 1000000L, 2, true), (http.Accounts![0].Name, http.Accounts[0].MaxUsdPerDay, http.Accounts[0].MaxTokensPerMonth, http.Accounts[0].MaxConcurrentRuns, http.Accounts[0].Driven));
        Assert.False(http.Accounts[1].Driven);
        Assert.Null(http.Accounts[1].Repositories);

        foreach (var bad in new[]
        {
            """[{"name":"owner"}]""", """[{"name":"Alice"}]""", """[{"name":"a"},{"name":"a"}]""", """[{"name":"a","max_usd_per_day":0}]""",
            """[{"name":"a","max_concurrent_runs":0}]""", """[{"name":"a","repositories":["relative/path"]}]""",
        })
            Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ChargehandException>(() => Load(bad)).Code);
    }

    [Theory]
    [InlineData("""{"schema":"profile/v1","http":{"api_key_secret":"k","accounts":[{"name":"alice","max_usd_per_day":5,"max_usd_per_month":50,"max_tokens_per_day":100,"max_tokens_per_month":1000,"max_concurrent_runs":2,"driven":true,"repositories":["/srv/a"]}]}}""", true)]
    [InlineData("""{"schema":"profile/v1","http":{"api_key_secret":"k","accounts":[{"name":"owner"}]}}""", false)]
    [InlineData("""{"schema":"profile/v1","http":{"api_key_secret":"k","accounts":[{"name":"Alice"}]}}""", false)]
    [InlineData("""{"schema":"profile/v1","http":{"api_key_secret":"k","accounts":[{"name":"a","key":"plaintext"}]}}""", false)] // keys never sit in the profile
    [InlineData("""{"schema":"profile/v1","http":{"api_key_secret":"k","accounts":[{"name":"a","max_usd_per_day":0}]}}""", false)]
    public void The_schema_checks_accounts(string profile, bool valid)
    {
        using var doc = JsonDocument.Parse(profile);
        Assert.Equal(valid, ConfigFileTests.ProfileSchema.Evaluate(doc.RootElement).IsValid);
    }

    [Fact]
    public void An_actor_is_absent_from_a_result_that_has_none()
    {
        var json = JsonSerializer.Serialize(Runs.Completed(), ContractJson.Options);
        Assert.DoesNotContain("actor", json, StringComparison.Ordinal);
        Assert.Matches(new Regex("\"actor\":\"alice\""), JsonSerializer.Serialize(Runs.Completed() with { Actor = "alice" }, ContractJson.Options));
    }
}
