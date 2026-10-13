using System.Net;
using System.Text.Json;
using Chargehand.Automation;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Server;
using Chargehand.Workspace;

namespace Chargehand.Tests;

/// <summary>The console's read routes that describe the server, its settings and its accounts (CHARGEHAND-209): authorisation, intake, repositories, accounts, system and the overview.</summary>
public class ConsoleViewsTests
{
    private static readonly string[] Routes =
    [
        "/v1/overview", "/v1/inbox", "/v1/batches/run-x", "/v1/batches/run-x/tasks/t1", "/v1/batches/run-x/tasks/t1/sessions/run-s1/transcript", "/v1/pulls", "/v1/analytics", "/v1/intake", "/v1/repositories", "/v1/accounts", "/v1/system",
    ];

    private static readonly string[] OwnerOnly = ["/v1/intake", "/v1/accounts", "/v1/system"];

    private static AccountSettings Alice(string[]? repositories = null, AccountModel? model = null, decimal? usdDay = null) =>
        new("alice", usdDay, Driven: true, Repositories: repositories, Model: model, AllowServerCredential: model is null);

    private static AccountSettings Bob() => new("bob", Driven: true, AllowServerCredential: true);

    private sealed class FixedHost(HostSnapshot? snapshot) : IHostSource
    {
        public Task<HostSnapshot?> ReadAsync(CancellationToken ct) => Task.FromResult(snapshot);
    }

    private static DrivenServices Services(DrivenRunTests.FakeEngine engine, HostSnapshot? host = null) =>
        new(engine, engine, new DrivenRunTests.FakeVolumes(_ => SessionStatus.Completed), _ => throw new NotSupportedException(), null,
            (_, _, _) => Task.FromResult(new DrivenCheckout("/srv/checkouts/repo", ConsoleRig.Commit, "https://github.com/o/r.git", "main")), new DrivenRunTests.NoEvidenceProblems(),
            name => $"value-of-{name}", Host: new FixedHost(host));

    // Authorisation

    [Fact]
    public async Task Every_console_route_needs_a_key_and_a_run_token_reaches_none()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()]);
        var repo = Runs.GitRepo(rig.Server.WorkerRoot);
        var token = rig.Server.Tokens.Issue(new RunTokenClaims("run-task", repo.Path, repo.Commit, null, null, DateTimeOffset.UtcNow.AddHours(1), "alice"), DateTimeOffset.UtcNow);
        using var anonymous = new HttpClient { BaseAddress = rig.Server.BaseAddress };
        using var session = rig.Server.As(token);
        using var wrong = rig.Server.As("wrong");

        foreach (var route in Routes)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await ConsoleRig.Status(anonymous, route));
            Assert.Equal(HttpStatusCode.Unauthorized, await ConsoleRig.Status(wrong, route));
            Assert.Equal(HttpStatusCode.Forbidden, await ConsoleRig.Status(session, route));
        }
    }

    [Fact]
    public async Task The_routes_that_describe_the_server_are_the_owners_and_an_account_is_refused()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()]);
        using var alice = rig.As("alice");

        foreach (var route in OwnerOnly)
        {
            Assert.Equal(HttpStatusCode.Forbidden, await ConsoleRig.Status(alice, route));
            Assert.Equal(HttpStatusCode.OK, await ConsoleRig.Status(rig.Owner, route));
        }
        foreach (var route in Routes.Except(OwnerOnly).Where(r => !r.Contains("run-x", StringComparison.Ordinal)))
            Assert.Equal(HttpStatusCode.OK, await ConsoleRig.Status(alice, route));
    }

    [Fact]
    public async Task The_console_has_no_verb_that_changes_anything()
    {
        await using var rig = await ConsoleRig.StartAsync();

        foreach (var route in Routes)
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
                Assert.Equal(HttpStatusCode.MethodNotAllowed, (await rig.Owner.SendAsync(new HttpRequestMessage(method, route))).StatusCode);
    }

    // Intake

    private static Profile IntakeProfile(string root) => Runs.Profile(root) with
    {
        Automation = new AutomationSettings(new AutomationGitHub(new Dictionary<string, AutomationRepository> { ["o/r"] = new("/srv/checkouts/r") }, ["octo"], MaxConcurrent: 1, MaxStartsPerDay: 2)),
    };

    [Fact]
    public async Task Intake_reads_what_the_poller_saved_without_a_review_comments_text()
    {
        await using var rig = await ConsoleRig.StartAsync(profile: IntakeProfile);
        var now = ConsoleRig.Now;
        var state = IntakeState.Load(rig.IntakePath);
        state.Tasks.Add(new IntakeTask("o/r", 12, "b1", "t1", now.AddHours(-3), "M", IntakePhase.Asked, AskedCommentId: 5));
        state.Tasks.Add(new IntakeTask("o/r", 13, "b2", "t1", now.AddHours(-1), "S"));
        state.Tasks.Add(new IntakeTask("o/r", 99, "b3", "t1", now.AddHours(-2), "M", IntakePhase.Done, PrUrl: "https://example.test/o/r/pull/99", Branch: "chargehand/b3-t1", Round: 1));
        state.Starts.AddRange([now.AddHours(-1), now.AddHours(-2), now.AddHours(-30)]);
        state.Handled.UnionWith(["label:o/r#12:1", "comment:o/r:5", "comment:o/r:6", "review:o/r:7", "round:o/r#99:8"]);
        state.Notified.UnionWith(["wait:comment:o/r:9", "cap:label:o/r#14:3"]);
        state.Pending.Add(new PendingReview("r7", "o/r", 99, "octo", "SECRET REVIEW BODY", "src/a.cs", 10, now.AddHours(-1)));
        state.CommentCursor["o/r"] = now.AddMinutes(-5);
        state.Save(now);

        var text = await ConsoleRig.Text(rig.Owner, "/v1/intake");
        var body = JsonSerializer.Deserialize<JsonElement>(text);

        Assert.Equal("intake/v1", body.GetProperty("contract_version").GetString());
        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.True(body.GetProperty("state_present").GetBoolean());
        Assert.Equal(("o/r", "/srv/checkouts/r"), (body.GetProperty("watched")[0].GetProperty("repository").GetString(), body.GetProperty("watched")[0].GetProperty("path").GetString()));
        Assert.Equal(["octo"], body.GetProperty("allowed_authors").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(("chargehand", "@chargehand", 120), (body.GetProperty("label").GetString(), body.GetProperty("mention").GetString(), body.GetProperty("poll_seconds").GetInt32()));
        Assert.Equal((1, 2, 3), (body.GetProperty("caps").GetProperty("max_concurrent").GetInt32(), body.GetProperty("caps").GetProperty("max_starts_per_day").GetInt32(), body.GetProperty("caps").GetProperty("max_review_rounds").GetInt32()));
        var starts = body.GetProperty("starts");
        Assert.Equal((2, 2, 0), (starts.GetProperty("last24h").GetInt32(), starts.GetProperty("cap").GetInt32(), starts.GetProperty("remaining").GetInt32()));
        Assert.Equal(now.AddHours(-2).AddDays(1), starts.GetProperty("next_slot_at").GetDateTimeOffset());
        Assert.Equal(1, body.GetProperty("running").GetInt32());
        var tasks = body.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal(["b2", "b3", "b1"], tasks.Select(t => t.GetProperty("batch_run_id").GetString()));
        Assert.Equal(("running", "done", "asked"), (tasks[0].GetProperty("phase").GetString(), tasks[1].GetProperty("phase").GetString(), tasks[2].GetProperty("phase").GetString()));
        Assert.True(tasks[2].GetProperty("awaiting_answer").GetBoolean());
        Assert.Equal((1, "chargehand/b3-t1"), (tasks[1].GetProperty("round").GetInt32(), tasks[1].GetProperty("branch").GetString()));
        var pending = Assert.Single(body.GetProperty("pending_reviews").EnumerateArray());
        Assert.Equal(("r7", "octo", "src/a.cs", 10), (pending.GetProperty("id").GetString(), pending.GetProperty("author").GetString(), pending.GetProperty("path").GetString(), pending.GetProperty("line").GetInt32()));
        Assert.DoesNotContain("SECRET REVIEW BODY", text, StringComparison.Ordinal);
        var handled = body.GetProperty("handled");
        Assert.Equal((5, 1, 2, 1, 1), (handled.GetProperty("total").GetInt32(), handled.GetProperty("labels").GetInt32(), handled.GetProperty("comments").GetInt32(), handled.GetProperty("reviews").GetInt32(), handled.GetProperty("rounds").GetInt32()));
        Assert.Equal([("cap", "label:o/r#14:3"), ("wait", "comment:o/r:9")],
            body.GetProperty("notices").EnumerateArray().Select(n => (n.GetProperty("reason").GetString()!, n.GetProperty("trigger").GetString()!)));
        Assert.True(body.GetProperty("comment_cursors").TryGetProperty("o/r", out _));
        Assert.NotEmpty(body.GetProperty("not_recorded").EnumerateArray());
    }

    [Fact]
    public async Task Intake_without_a_state_file_has_every_start_free_and_without_automation_it_is_off()
    {
        await using (var rig = await ConsoleRig.StartAsync(profile: IntakeProfile))
        {
            var body = await ConsoleRig.Get(rig.Owner, "/v1/intake");
            Assert.True(body.GetProperty("enabled").GetBoolean());
            Assert.False(body.GetProperty("state_present").GetBoolean());
            Assert.Equal(2, body.GetProperty("starts").GetProperty("remaining").GetInt32());
            Assert.Equal(0, body.GetProperty("tasks").GetArrayLength());
        }
        await using var off = await ConsoleRig.StartAsync();
        var disabled = await ConsoleRig.Get(off.Owner, "/v1/intake");
        Assert.False(disabled.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, disabled.GetProperty("watched").GetArrayLength());
    }

    [Fact]
    public async Task An_intake_state_file_that_cannot_be_read_is_reported_and_does_not_fail_the_route()
    {
        await using var rig = await ConsoleRig.StartAsync(profile: IntakeProfile);
        Directory.CreateDirectory(Path.GetDirectoryName(rig.IntakePath)!);
        await File.WriteAllTextAsync(rig.IntakePath, "{ not json");

        var body = await ConsoleRig.Get(rig.Owner, "/v1/intake");

        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.True(body.GetProperty("state_present").GetBoolean());
        Assert.StartsWith("the state file could not be read", body.GetProperty("not_recorded")[0].GetString(), StringComparison.Ordinal);
        Assert.Equal(2, body.GetProperty("starts").GetProperty("remaining").GetInt32());
    }

    // Repositories

    [Fact]
    public async Task Repositories_list_the_checkouts_with_head_remote_without_a_credential_ecosystem_and_open_pull_requests()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice(), Bob()], profile: root => Runs.Profile(root) with
        {
            RepositoryRoots = [Path.Combine(root, "roots-a"), Path.Combine(root, "roots-b")],
            Driven = new DrivenSettings(Integrator: new DrivenIntegrator(Repositories: new Dictionary<string, string> { ["o/r"] = "merge" })),
            Automation = new AutomationSettings(new AutomationGitHub(new Dictionary<string, AutomationRepository> { ["o/r"] = new(Path.Combine(root, "roots-a", "repo")) }, ["octo"])),
        });
        var (a, b) = (Path.Combine(rig.Root, "roots-a"), Path.Combine(rig.Root, "roots-b"));
        var one = Runs.GitRepo(a);
        Runs.SetOrigin(one, "https://x-access-token:TOKEN-IN-THE-URL@github.com/o/r.git");
        File.WriteAllText(Path.Combine(one.Path, "Chargehand.slnx"), "<Solution />");
        Directory.CreateDirectory(Path.Combine(one.Path, "scripts"));
        File.WriteAllText(Path.Combine(one.Path, "scripts", "check.sh"), "#!/bin/sh\n");
        var two = Runs.GitRepo(b);
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b1", "t1", pr: ConsoleSeed.Pull(7)));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b2", "t1", actor: "bob", pr: ConsoleSeed.Pull(8)));
        var merged = ConsoleSeed.Learned("b3", "t1", pr: ConsoleSeed.Pull(9));
        await ConsoleSeed.LearnAsync(rig, merged);
        await ConsoleSeed.OutcomeAsync(rig, merged, PrOutcomes.Merged);

        var text = await ConsoleRig.Text(rig.Owner, "/v1/repositories");
        var body = JsonSerializer.Deserialize<JsonElement>(text);

        Assert.Equal("repositories/v1", body.GetProperty("contract_version").GetString());
        Assert.Equal([a, b], body.GetProperty("roots").EnumerateArray().Select(r => r.GetProperty("path").GetString()));
        var repositories = body.GetProperty("repositories").EnumerateArray().ToDictionary(r => r.GetProperty("path").GetString()!);
        Assert.Equal(2, repositories.Count);
        var first = repositories[Path.GetFullPath(one.Path)];
        Assert.Equal(one.Commit, first.GetProperty("head").GetString());
        Assert.Equal("https://github.com/o/r.git", first.GetProperty("remote").GetString());
        Assert.Equal("o/r", first.GetProperty("repository").GetString());
        Assert.False(string.IsNullOrEmpty(first.GetProperty("default_branch").GetString()));
        Assert.Equal(["dotnet"], first.GetProperty("ecosystems").EnumerateArray().Select(e => e.GetProperty("name").GetString()));
        Assert.Equal(("scripts/check.sh", "merge", true), (first.GetProperty("verify").GetProperty("source").GetString(), first.GetProperty("integrator_mode").GetString(), first.GetProperty("watched_by_intake").GetBoolean()));
        Assert.Equal(["sh", "scripts/check.sh"], first.GetProperty("verify").GetProperty("command").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal([8, 7], first.GetProperty("open_pull_requests").EnumerateArray().Select(p => p.GetProperty("number").GetInt32()).OrderDescending());
        var second = repositories[Path.GetFullPath(two.Path)];
        Assert.Equal(("off", false), (second.GetProperty("integrator_mode").GetString(), second.GetProperty("watched_by_intake").GetBoolean()));
        Assert.False(second.TryGetProperty("remote", out _));
        Assert.DoesNotContain("TOKEN-IN-THE-URL", text, StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_account_sees_the_checkouts_it_may_use_and_only_its_own_pull_requests()
    {
        await using var rig = await ConsoleRig.StartAsync(accountsFor: root => [Alice([Path.Combine(root, "roots-a")]), Bob()],
            profile: root => Runs.Profile(root) with { RepositoryRoots = [Path.Combine(root, "roots-a"), Path.Combine(root, "roots-b")] });
        var one = Runs.GitRepo(Path.Combine(rig.Root, "roots-a"));
        var two = Runs.GitRepo(Path.Combine(rig.Root, "roots-b"));
        Runs.SetOrigin(one, "https://github.com/o/r.git");
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b1", "t1", actor: "alice", pr: ConsoleSeed.Pull(7)));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b2", "t1", actor: "bob", pr: ConsoleSeed.Pull(8)));
        using var alice = rig.As("alice");
        using var bob = rig.As("bob");

        var alices = (await ConsoleRig.Get(alice, "/v1/repositories")).GetProperty("repositories").EnumerateArray().ToList();
        var bobs = (await ConsoleRig.Get(bob, "/v1/repositories")).GetProperty("repositories").EnumerateArray().ToList();

        Assert.Equal([Path.GetFullPath(one.Path)], alices.Select(r => r.GetProperty("path").GetString()));
        Assert.Equal([7], alices[0].GetProperty("open_pull_requests").EnumerateArray().Select(p => p.GetProperty("number").GetInt32()));
        Assert.Equal([Path.GetFullPath(one.Path), Path.GetFullPath(two.Path)], bobs.Select(r => r.GetProperty("path").GetString()).Order());
        Assert.Equal([8], bobs.Single(r => r.GetProperty("path").GetString() == Path.GetFullPath(one.Path)).GetProperty("open_pull_requests").EnumerateArray().Select(p => p.GetProperty("number").GetInt32()));
    }

    // Accounts

    [Fact]
    public async Task Accounts_show_caps_spend_keys_and_whether_a_credential_resolves_and_never_a_key()
    {
        var alice = Alice(model: new AccountModel(ApiKeySecret: "alice-key-item"), usdDay: 5m);
        var carol = new AccountSettings("carol", Model: new AccountModel(ApiKeySecret: "carol-key-item", BaseUrl: "https://gateway.example/anthropic"));
        var dave = new AccountSettings("dave");
        await using var rig = await ConsoleRig.StartAsync(accounts: [alice, carol, dave], secretPresent: item => item == "alice-key-item");
        var first = rig.Server.Keys.Create("alice", "laptop");
        var second = rig.Server.Keys.Create("alice", "ci");
        rig.Server.Keys.Revoke(second.Id);
        var start = ConsoleSeed.Start("run-a", Runs.DraftRequest(), "alice", ConsoleRig.Now.AddHours(-2));
        await rig.Log.AppendAsync(start, default);
        await rig.Log.AppendAsync(ConsoleSeed.Finish(start, ConsoleSeed.Result("run-a", ResultStatus.Completed, "s", usage: new Usage(900, 100, 0, 0, 1.25m), actor: "alice"), ConsoleRig.Now.AddHours(-1)), default);

        var text = await ConsoleRig.Text(rig.Owner, "/v1/accounts");
        var body = JsonSerializer.Deserialize<JsonElement>(text);

        Assert.Equal("accounts/v1", body.GetProperty("contract_version").GetString());
        var accounts = body.GetProperty("accounts").EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!);
        Assert.Equal(["owner", "alice", "carol", "dave"], body.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        Assert.True(accounts["owner"].GetProperty("owner").GetBoolean());
        Assert.Equal("server", accounts["owner"].GetProperty("credential").GetProperty("mode").GetString());
        var a = accounts["alice"];
        Assert.Equal(5m, a.GetProperty("limits").GetProperty("max_usd_per_day").GetDecimal());
        Assert.Equal(1.25m, a.GetProperty("spend").GetProperty("day").GetProperty("usd").GetDecimal());
        Assert.Equal(1000, a.GetProperty("spend").GetProperty("day").GetProperty("tokens").GetInt32());
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero), a.GetProperty("spend").GetProperty("day").GetProperty("resets_at").GetDateTimeOffset());
        Assert.Equal(("own_key", true), (a.GetProperty("credential").GetProperty("mode").GetString(), a.GetProperty("credential").GetProperty("present").GetBoolean()));
        var keys = a.GetProperty("keys").EnumerateArray().ToList();
        Assert.Equal([first.Id, second.Id], keys.Select(k => k.GetProperty("id").GetString()));
        Assert.Equal(["laptop", "ci"], keys.Select(k => k.GetProperty("label").GetString()));
        Assert.False(keys[0].TryGetProperty("revoked", out _));
        Assert.True(keys[1].TryGetProperty("revoked", out _));
        Assert.Equal(("own_gateway", false), (accounts["carol"].GetProperty("credential").GetProperty("mode").GetString(), accounts["carol"].GetProperty("credential").GetProperty("present").GetBoolean()));
        Assert.Equal("none", accounts["dave"].GetProperty("credential").GetProperty("mode").GetString());
        Assert.False(accounts["dave"].GetProperty("credential").TryGetProperty("present", out _));
        foreach (var stored in rig.Server.Keys.List())
        {
            Assert.DoesNotContain(stored.Hash, text, StringComparison.Ordinal);
            Assert.DoesNotContain(stored.Salt, text, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(first.Key.Split('.')[2], text, StringComparison.Ordinal);
        Assert.DoesNotContain(first.Key, text, StringComparison.Ordinal);
        Assert.DoesNotContain("alice-key-item", text, StringComparison.Ordinal);
        Assert.DoesNotContain("carol-key-item", text, StringComparison.Ordinal);
        Assert.DoesNotContain("gateway.example", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_credential_check_without_a_probe_says_nothing_about_presence()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice(model: new AccountModel(ApiKeySecret: "alice-key-item"))]);

        var alice = (await ConsoleRig.Get(rig.Owner, "/v1/accounts")).GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "alice");

        Assert.Equal("own_key", alice.GetProperty("credential").GetProperty("mode").GetString());
        Assert.False(alice.GetProperty("credential").TryGetProperty("present", out _));
    }

    // System

    private static readonly string[] Secrets =
    [
        "super-secret-api-item", "claude-key-item", "push-item", "runner-key-item", "pem-item", "hdr-item", "URLSECRET", "token=abc", "path-secret", "pub-item", "sec-item", "forge-secret-path", "command-secret",
    ];

    private static Profile SecretProfile(string root) => Runs.Profile(root) with
    {
        Http = new HttpSettings("super-secret-api-item", Accounts: [Alice()], AllowedHosts: ["chargehand.internal"]),
        ClaudeCode = new ClaudeCodeSettings("2.1.283", ApiKeySecret: "claude-key-item", BaseUrl: "https://gateway.internal/anthropic"),
        Telemetry = new TelemetrySettings("https://otlp.example/x", "pub-item", "sec-item"),
        McpServers = new Dictionary<string, McpServerSettings>
        {
            ["memory"] = new(Url: "https://mcp.example/x?token=URLSECRET", Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer {secret:hdr-item}" }),
            ["local"] = new(Command: ["tool", "--key=command-secret"]),
        },
        Driven = new DrivenSettings(true, Images: ["registry.test/session@sha256:" + new string('a', 64), "registry.test/loose:latest"], PushSecret: "push-item",
            Runner: new DrivenRunner("http://runner.internal:4310/base?token=abc", "runner-key-item"), Network: new DrivenNetwork(Allow: ["api.example.test"], ModelUrl: "https://gw.example/path-secret"),
            Integrator: new DrivenIntegrator(AppId: 1, InstallationId: 2, PrivateKeySecret: "pem-item", Repositories: new Dictionary<string, string> { ["o/r"] = "ask" }),
            Forge: new DrivenForge("github", "https://ghe.example/forge-secret-path/api/v3")),
    };

    [Fact]
    public async Task System_shows_the_redacted_profile_presets_images_and_the_runner_with_its_live_containers()
    {
        var engine = new DrivenRunTests.FakeEngine
        {
            Resources = new(["chargehand-run-s2", "chargehand-egress-run-live", "chargehand-svc-pg-run-live", "chargehand-prep-run-s2", "chargehand-stray"], ["chargehand-net-run-live"], ["chargehand-out-run-s2"]),
        };
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()], profile: SecretProfile, services: _ => Services(engine, new HostSnapshot(8000, 16000, 2000, 1500, 0.5, 4)));
        var request = ConsoleSeed.BatchRequest(new DrivenTask("t1", Goal: "one"));
        new BatchStateStore(rig.BatchDirectory).Save(new BatchState("run-live", ConsoleRig.Now.AddMinutes(-9), new string('0', 32), request, "TOKEN-KEY",
            [new TaskEntry("t1", null, "one", TaskPhase.Started, "run-s2", "chargehand/run-s2", "chargehand-run-s2", Runs: ["run-s2"])], [], Actor: "alice"));

        var text = await ConsoleRig.Text(rig.Owner, "/v1/system");
        var body = JsonSerializer.Deserialize<JsonElement>(text);

        Assert.Equal("system/v1", body.GetProperty("contract_version").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("version").GetString()));
        Assert.False(body.GetProperty("halted").GetBoolean());
        var profile = body.GetProperty("profile");
        Assert.Equal(("claude_code", "draft"), (profile.GetProperty("runtime").GetString(), profile.GetProperty("default_preset").GetString()));
        Assert.Equal(("api_key", true), (profile.GetProperty("claude_code").GetProperty("credential").GetString(), profile.GetProperty("claude_code").GetProperty("gateway").GetBoolean()));
        Assert.True(profile.GetProperty("http").GetProperty("api_key_set").GetBoolean());
        Assert.Equal(["alice"], profile.GetProperty("http").GetProperty("accounts").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal([("local"), ("memory")], profile.GetProperty("mcp_servers").EnumerateArray().Select(m => m.GetString()));
        var driven = profile.GetProperty("driven");
        Assert.Equal(("default", "http://runner.internal:4310"), (driven.GetProperty("runners")[0].GetProperty("name").GetString(), driven.GetProperty("runners")[0].GetProperty("host").GetString()));
        Assert.True(driven.GetProperty("push_secret_set").GetBoolean());
        Assert.True(driven.GetProperty("integrator").GetProperty("app_configured").GetBoolean());
        Assert.Equal("ask", driven.GetProperty("integrator").GetProperty("repositories").GetProperty("o/r").GetString());
        Assert.Equal("ghe.example", new Uri(driven.GetProperty("forge_host").GetString()!).Host);
        Assert.Contains("draft", body.GetProperty("presets").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        var draft = body.GetProperty("presets").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "draft");
        Assert.False(draft.GetProperty("driven").GetBoolean());
        Assert.True(body.GetProperty("presets").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "driven").GetProperty("driven").GetBoolean());

        var images = body.GetProperty("images").EnumerateArray().ToList();
        Assert.Equal((true, false), (images[0].GetProperty("pinned").GetBoolean(), images[1].GetProperty("pinned").GetBoolean()));
        Assert.Equal("registry.test/session", images[0].GetProperty("name").GetString());

        var runner = body.GetProperty("runner");
        Assert.Equal((true, true, 5), (runner.GetProperty("configured").GetBoolean(), runner.GetProperty("reachable").GetBoolean(), runner.GetProperty("container_count").GetInt32()));
        var containers = runner.GetProperty("containers").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!);
        Assert.Equal(("session", "run-live", "t1", "alice"), (containers["chargehand-run-s2"].GetProperty("kind").GetString(), containers["chargehand-run-s2"].GetProperty("batch_run_id").GetString(),
            containers["chargehand-run-s2"].GetProperty("task_id").GetString(), containers["chargehand-run-s2"].GetProperty("actor").GetString()));
        Assert.Equal(("egress", "run-live"), (containers["chargehand-egress-run-live"].GetProperty("kind").GetString(), containers["chargehand-egress-run-live"].GetProperty("batch_run_id").GetString()));
        Assert.Equal(("service", "run-live"), (containers["chargehand-svc-pg-run-live"].GetProperty("kind").GetString(), containers["chargehand-svc-pg-run-live"].GetProperty("batch_run_id").GetString()));
        Assert.Equal(("workspace", "run-live"), (containers["chargehand-prep-run-s2"].GetProperty("kind").GetString(), containers["chargehand-prep-run-s2"].GetProperty("batch_run_id").GetString()));
        Assert.Equal("other", containers["chargehand-stray"].GetProperty("kind").GetString());
        Assert.Equal(["chargehand-net-run-live"], runner.GetProperty("networks").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal(["chargehand-out-run-s2"], runner.GetProperty("volumes").EnumerateArray().Select(n => n.GetString()));
        var host = runner.GetProperty("host_snapshot");
        Assert.Equal((8000, 6976, 0.5, 4), (host.GetProperty("available_mb").GetInt32(), host.GetProperty("free_after_reserve_mb").GetInt32(), host.GetProperty("load1").GetDouble(), host.GetProperty("cpus").GetInt32()));

        foreach (var secret in Secrets.Append("TOKEN-KEY"))
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task System_says_so_when_the_runner_does_not_answer_and_when_driven_sessions_are_off()
    {
        var engine = new DrivenRunTests.FakeEngine { OnList = new ChargehandException(ErrorCode.ContainerUnavailable, "the runner could not be reached: connection refused") };
        await using (var down = await ConsoleRig.StartAsync(profile: SecretProfile, services: _ => Services(engine)))
        {
            var runner = (await ConsoleRig.Get(down.Owner, "/v1/system")).GetProperty("runner");
            Assert.False(runner.GetProperty("reachable").GetBoolean());
            Assert.Contains("could not be reached", runner.GetProperty("error").GetString(), StringComparison.Ordinal);
            Assert.Equal(0, runner.GetProperty("containers").GetArrayLength());
        }
        await using var off = await ConsoleRig.StartAsync();
        var none = (await ConsoleRig.Get(off.Owner, "/v1/system")).GetProperty("runner");
        Assert.False(none.GetProperty("configured").GetBoolean());
        Assert.False(none.TryGetProperty("reachable", out _));
    }

    [Fact]
    public void A_profile_view_has_no_secret_url_path_header_or_command()
    {
        var view = JsonSerializer.Serialize(ConsoleSystem.View(SecretProfile("/tmp/work")), ContractJson.Options);

        foreach (var secret in Secrets)
            Assert.DoesNotContain(secret, view, StringComparison.Ordinal);
    }

    // Overview

    [Fact]
    public async Task The_owners_overview_has_the_host_the_runner_the_sessions_the_accounts_and_the_inbox()
    {
        var engine = new DrivenRunTests.FakeEngine();
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice(usdDay: 5m)], profile: SecretProfile, services: _ => Services(engine, new HostSnapshot(8000, 16000, 2000, 1500, 0.5, 4)), hold: true);
        await ConsoleSeed.BatchAsync(rig.Log, "b-q", null, ConsoleRig.Now.AddHours(-5), new SeedTask("t1", "needs_input", Questions: ["Which?"]));
        Assert.Equal(HttpStatusCode.Accepted, (await rig.Server.PostAsync(Runs.DraftRequest(), 0)).StatusCode);

        var body = await ConsoleRig.Get(rig.Owner, "/v1/overview");

        Assert.Equal("overview/v1", body.GetProperty("contract_version").GetString());
        Assert.Equal(("owner", "owner"), (body.GetProperty("scope").GetString(), body.GetProperty("account").GetString()));
        var server = body.GetProperty("server");
        Assert.Equal((false, 1, 10), (server.GetProperty("halted").GetBoolean(), server.GetProperty("runs_held").GetInt32(), server.GetProperty("max_unfinished").GetInt32()));
        Assert.Equal(1, server.GetProperty("runs_queued").GetInt32() + server.GetProperty("runs_running").GetInt32());
        var driven = body.GetProperty("driven");
        Assert.True(driven.GetProperty("enabled").GetBoolean());
        Assert.True(driven.GetProperty("runner").GetProperty("reachable").GetBoolean());
        Assert.Equal((8000, 6976, 1024), (driven.GetProperty("host").GetProperty("available_mb").GetInt32(), driven.GetProperty("host").GetProperty("free_after_reserve_mb").GetInt32(), driven.GetProperty("host").GetProperty("reserve_mb").GetInt32()));
        Assert.Equal((0, 4), (driven.GetProperty("sessions").GetProperty("running").GetInt32(), driven.GetProperty("sessions").GetProperty("max_parallel_total").GetInt32()));
        Assert.False(driven.GetProperty("rate_limit").GetProperty("paused").GetBoolean());
        Assert.Equal("owner", body.GetProperty("standing").GetProperty("name").GetString());
        Assert.Equal(["owner", "alice"], body.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(5m, body.GetProperty("accounts")[1].GetProperty("limits").GetProperty("max_usd_per_day").GetDecimal());
        Assert.Equal((1, 0, 0, 1), (body.GetProperty("inbox").GetProperty("questions").GetInt32(), body.GetProperty("inbox").GetProperty("pull_requests").GetInt32(), body.GetProperty("inbox").GetProperty("failures").GetInt32(),
            body.GetProperty("inbox").GetProperty("total").GetInt32()));

        Assert.Equal(HttpStatusCode.OK, (await rig.Owner.PostAsync("/v1/halt", null)).StatusCode);
        Assert.True((await ConsoleRig.Get(rig.Owner, "/v1/overview")).GetProperty("server").GetProperty("halted").GetBoolean());
    }

    [Fact]
    public async Task An_accounts_overview_holds_its_own_runs_and_spend_and_nothing_of_the_host_or_other_accounts()
    {
        var engine = new DrivenRunTests.FakeEngine();
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice(usdDay: 5m), Bob()], profile: SecretProfile, services: _ => Services(engine, new HostSnapshot(8000, 16000, 2000, 1500, 0.5, 4)), hold: true);
        await ConsoleSeed.BatchAsync(rig.Log, "a-q", "alice", ConsoleRig.Now.AddHours(-5), new SeedTask("t1", "needs_input", Questions: ["Alice?"]));
        await ConsoleSeed.BatchAsync(rig.Log, "b-q", "bob", ConsoleRig.Now.AddHours(-5), new SeedTask("t1", "needs_input", Questions: ["Bob?"]));
        using var alice = rig.As("alice");
        Assert.Equal(HttpStatusCode.Accepted, (await rig.Server.PostAsync(Runs.DraftRequest(), 0)).StatusCode);
        var post = new HttpRequestMessage(HttpMethod.Post, "/v1/runs") { Content = Body(Runs.DraftRequest()) };
        post.Headers.Add("Prefer", "wait=0");
        Assert.Equal(HttpStatusCode.Accepted, (await alice.SendAsync(post)).StatusCode);

        var text = await ConsoleRig.Text(alice, "/v1/overview");
        var body = JsonSerializer.Deserialize<JsonElement>(text);

        Assert.Equal(("account", "alice"), (body.GetProperty("scope").GetString(), body.GetProperty("account").GetString()));
        Assert.Equal(1, body.GetProperty("server").GetProperty("runs_held").GetInt32());
        Assert.Equal(2, (await ConsoleRig.Get(rig.Owner, "/v1/overview")).GetProperty("server").GetProperty("runs_held").GetInt32());
        Assert.False(body.GetProperty("server").TryGetProperty("max_concurrent_runs", out _));
        Assert.True(body.GetProperty("driven").GetProperty("enabled").GetBoolean());
        Assert.False(body.GetProperty("driven").TryGetProperty("runner", out _));
        Assert.False(body.GetProperty("driven").TryGetProperty("host", out _));
        Assert.False(body.GetProperty("driven").GetProperty("sessions").TryGetProperty("max_parallel_total", out _));
        Assert.False(body.TryGetProperty("accounts", out _));
        Assert.Equal(("alice", 5m), (body.GetProperty("standing").GetProperty("name").GetString(), body.GetProperty("standing").GetProperty("limits").GetProperty("max_usd_per_day").GetDecimal()));
        Assert.Equal(1, body.GetProperty("inbox").GetProperty("questions").GetInt32());
        foreach (var other in new[] { "Bob?", "bob", "8000", "reserve_mb" })
            Assert.DoesNotContain(other, text, StringComparison.Ordinal);
    }

    private static System.Net.Http.Json.JsonContent Body(RunRequest request) => System.Net.Http.Json.JsonContent.Create(request, options: ContractJson.Options);
}
