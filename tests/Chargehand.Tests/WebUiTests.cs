using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>The web UI (ADR 0054): off unless asked for, served with a strict policy, calling only the server's own routes; and the routes it needs.</summary>
public partial class WebUiTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static string UiDirectory => Repo.Path("src", "Chargehand.Server", "Ui");

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
    public async Task The_ui_is_off_unless_the_profile_turns_it_on()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        using var anonymous = Plain(s);

        foreach (var path in new[] { "/ui", "/ui/", "/ui/main.js" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await s.Http.GetAsync(path)).StatusCode);
        }
    }

    [Fact]
    public async Task The_ui_is_served_without_a_key_under_a_strict_policy()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), ui: true);
        using var anonymous = Plain(s);

        var page = await anonymous.GetAsync("/ui/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html; charset=utf-8", page.Content.Headers.ContentType!.ToString());
        Assert.Contains("<title>chargehand</title>", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(await page.Content.ReadAsStringAsync(), await (await anonymous.GetAsync("/ui/index.html")).Content.ReadAsStringAsync());

        foreach (var path in new[] { "/ui/", "/ui/main.js", "/ui/app.css", "/ui/missing.js" })
        {
            var res = await anonymous.GetAsync(path);
            Assert.Equal(WebUi.ContentSecurityPolicy, res.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        }
        Assert.Equal("text/javascript; charset=utf-8", (await anonymous.GetAsync("/ui/main.js")).Content.Headers.ContentType!.ToString());
        Assert.Equal("text/css; charset=utf-8", (await anonymous.GetAsync("/ui/app.css")).Content.Headers.ContentType!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/ui/learn.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/ui/missing.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/ui/..%2Fappsettings.json")).StatusCode);
    }

    [Fact]
    public async Task The_ui_path_without_a_slash_redirects_and_opens_nothing_else()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), ui: true);
        using var anonymous = Plain(s, follow: false);

        var redirect = await anonymous.GetAsync("/ui");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/ui/", redirect.Headers.Location!.OriginalString);
        // Only /ui and below skip the key; a neighbour of that name and every API route still need it.
        foreach (var path in new[] { "/uix", "/ui-other/", "/v1/runs", "/v1/accounts/me", "/v1/mcp" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await anonymous.PostAsync("/ui/", null)).StatusCode);
    }

    [Fact]
    public void The_policy_allows_no_inline_script_no_eval_and_no_other_origin()
    {
        var directives = WebUi.ContentSecurityPolicy.Split(';', StringSplitOptions.TrimEntries).ToDictionary(d => d.Split(' ')[0], d => d[(d.IndexOf(' ') + 1)..]);

        Assert.Equal("'none'", directives["default-src"]);
        Assert.Equal("'self'", directives["script-src"]);
        Assert.Equal("'self'", directives["style-src"]);
        Assert.Equal("'self'", directives["connect-src"]);
        Assert.Equal("'none'", directives["frame-ancestors"]);
        Assert.DoesNotContain("unsafe", WebUi.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("*", WebUi.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("http", WebUi.ContentSecurityPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public void The_embedded_files_are_the_files_in_the_folder()
    {
        var onDisk = Directory.EnumerateFiles(UiDirectory, "*", SearchOption.AllDirectories).Select(f => System.IO.Path.GetRelativePath(UiDirectory, f).Replace('\\', '/')).Order().ToList();

        Assert.Contains("index.html", onDisk);
        Assert.Equal(onDisk, WebUi.Paths.Order().ToList());
    }

    [GeneratedRegex(@"(https?:)?//[A-Za-z0-9]")]
    private static partial Regex Origin();

    [GeneratedRegex(@"\b(?:innerHTML|outerHTML|insertAdjacentHTML|document\.write|eval\(|new Function|importScripts|WebSocket|XMLHttpRequest|EventSource|sendBeacon|cookie)\b")]
    private static partial Regex Forbidden();

    [GeneratedRegex(@"\bimport\s*(?:[^'""]*from\s*)?['""]([^'""]+)['""]")]
    private static partial Regex Import();

    [Fact]
    public void No_static_file_names_an_external_origin_or_runs_inline_code()
    {
        foreach (var path in WebUi.Paths)
        {
            var text = System.Text.Encoding.UTF8.GetString(WebUi.Read(path));
            // A URL with a host in a literal would be a request to another origin; the code builds the server's own paths only.
            Assert.False(Origin().IsMatch(text), $"{path} names another origin");
            switch (System.IO.Path.GetExtension(path))
            {
                case ".html":
                    Assert.DoesNotContain("<style", text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("style=", text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("javascript:", text, StringComparison.OrdinalIgnoreCase);
                    Assert.Empty(Regex.Matches(text, @"\son\w+\s*=", RegexOptions.IgnoreCase));
                    // Every script has a src; none is inline.
                    Assert.All(Regex.Matches(text, @"<script\b[^>]*>").Select(m => m.Value), tag => Assert.Contains("src=\"", tag, StringComparison.Ordinal));
                    Assert.All(Regex.Matches(text, @"(?:src|href)=""([^""]*)""").Select(m => m.Groups[1].Value), url => Assert.True(!url.Contains(':') || url.StartsWith("data:", StringComparison.Ordinal), url));
                    break;
                case ".css":
                    Assert.DoesNotContain("@import", text, StringComparison.Ordinal);
                    Assert.All(Regex.Matches(text, @"url\(([^)]*)\)").Select(m => m.Groups[1].Value.Trim('"', '\'')), url => Assert.StartsWith("data:", url, StringComparison.Ordinal));
                    break;
                case ".js":
                    Assert.Empty(Forbidden().Matches(text).Select(m => m.Value));
                    Assert.All(Import().Matches(text).Select(m => m.Groups[1].Value), specifier => Assert.StartsWith("./", specifier, StringComparison.Ordinal));
                    // One place talks to the network, and it is the one that adds the key.
                    Assert.True(path == "api.js" || !text.Contains("fetch(", StringComparison.Ordinal), $"{path} calls fetch");
                    break;
            }
        }
    }

    [Fact]
    public void The_key_is_kept_in_session_storage_and_nowhere_else()
    {
        var api = System.Text.Encoding.UTF8.GetString(WebUi.Read("api.js"));
        Assert.Contains("sessionStorage.setItem(KEY", api, StringComparison.Ordinal);
        foreach (var path in WebUi.Paths.Where(p => p.EndsWith(".js", StringComparison.Ordinal)))
        {
            var text = System.Text.Encoding.UTF8.GetString(WebUi.Read(path));
            // localStorage holds a repository's web address, never the key.
            Assert.True(path == "citations.js" || !text.Contains("localStorage", StringComparison.Ordinal), $"{path} uses localStorage");
            Assert.DoesNotContain("document.cookie", text, StringComparison.Ordinal);
        }
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
