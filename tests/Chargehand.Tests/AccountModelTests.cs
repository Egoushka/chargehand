using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.ClaudeCode;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;
using Chargehand.Runtime;
using Chargehand.Server;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Chargehand.Tests;

/// <summary>An account brings its own model credential (ADR 0053): which runs use whose key, what is refused, and that no run sees another account's key. Keys here are fakes.</summary>
public class AccountModelTests
{
    private const string Image = "ghcr.io/example/session@sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";

    private static AccountSettings Own(string name, string keyItem = "alice-key", string? baseUrl = null) => new(name, Model: new AccountModel(keyItem, baseUrl));

    private static async Task<JsonElement> Body(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<HttpResponseMessage> Post(HttpClient http, RunRequest request)
    {
        var post = new HttpRequestMessage(HttpMethod.Post, "/v1/runs") { Content = JsonContent.Create(request, options: ContractJson.Options) };
        post.Headers.Add("Prefer", "wait=30");
        return await http.SendAsync(post);
    }

    [Fact]
    public void The_profile_declares_an_accounts_model_and_the_server_credential_switch()
    {
        using var dir = new TempDir();
        Profile Load(string accounts) => Profile.Load(dir.Write("p.json", """{"schema":"profile/v1","http":{"api_key_secret":"k","accounts":""" + accounts + "}}"));

        var accounts = Load("""[{"name":"alice","model":{"api_key_secret":"alice-key"}},{"name":"gw","model":{"api_key_secret":"gw-key","base_url":"https://gateway.example.com/anthropic"}},{"name":"shared","allow_server_credential":true},{"name":"bare"}]""").Http!.Accounts!;

        Assert.Equal(("alice-key", (string?)null), (accounts[0].Model!.ApiKeySecret, accounts[0].Model!.BaseUrl));
        Assert.Equal("https://gateway.example.com/anthropic", accounts[1].Model!.BaseUrl);
        Assert.True(accounts[2].AllowServerCredential);
        Assert.False(accounts[3].AllowServerCredential);
        Assert.Null(accounts[3].Model);
    }

    [Theory]
    [InlineData("""[{"name":"a","model":{"oauth_token_secret":"t","api_key_secret":"k"}}]""", "subscription token")]
    [InlineData("""[{"name":"a","model":{"oauth_token_secret":"t"}}]""", "subscription token")]
    [InlineData("""[{"name":"a","model":{}}]""", "api_key_secret is required")]
    [InlineData("""[{"name":"a","model":{"api_key_secret":"k","base_url":"ftp://x"}}]""", "base_url")]
    [InlineData("""[{"name":"a","model":{"api_key_secret":"k","base_url":"https://user:pw@x.example/a"}}]""", "base_url")]
    [InlineData("""[{"name":"a","model":{"api_key_secret":"k","base_url":"https://x.example/a?key=1"}}]""", "base_url")]
    public void A_model_that_cannot_work_is_refused_with_its_reason(string accounts, string reason)
    {
        using var dir = new TempDir();
        var e = Assert.Throws<ChargehandException>(() => Profile.Load(dir.Write("p.json", """{"schema":"profile/v1","http":{"api_key_secret":"k","accounts":""" + accounts + "}}")));
        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Contains(reason, e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"name":"a","model":{"api_key_secret":"k"}}""", true)]
    [InlineData("""{"name":"a","model":{"api_key_secret":"k","base_url":"https://gateway.example.com/anthropic"},"allow_server_credential":false}""", true)]
    [InlineData("""{"name":"a","allow_server_credential":true}""", true)]
    [InlineData("""{"name":"a","model":{"oauth_token_secret":"t"}}""", false)]
    [InlineData("""{"name":"a","model":{}}""", false)]
    [InlineData("""{"name":"a","model":{"api_key_secret":"k","key":"sk-plaintext"}}""", false)] // keys never sit in the profile
    [InlineData("""{"name":"a","model":{"api_key_secret":"k","base_url":"https://u:p@x.example"}}""", false)]
    public void The_schema_checks_an_accounts_model(string account, bool valid)
    {
        using var doc = JsonDocument.Parse("""{"schema":"profile/v1","http":{"api_key_secret":"k","accounts":[""" + account + "]}}");
        Assert.Equal(valid, ConfigFileTests.ProfileSchema.Evaluate(doc.RootElement).IsValid);
    }

    [Fact]
    public void The_owner_a_cli_run_and_a_permitted_account_use_the_servers_credential_and_a_model_account_its_own()
    {
        var model = new AccountModel("alice-key");
        var credentials = new AccountCredentials([new("alice", Model: model), new("shared", AllowServerCredential: true), new("bare")]);

        Assert.Null(credentials.For(null));
        Assert.Null(credentials.For("owner"));
        Assert.Null(credentials.For("shared"));
        Assert.Same(model, credentials.For("alice"));
        foreach (var refused in new[] { "bare", "removed" })
            Assert.Equal(ErrorCode.CredentialUnavailable, Assert.Throws<ChargehandException>(() => credentials.For(refused)).Code);
        Assert.Null(new AccountCredentials(null).For("owner"));
    }

    [Fact]
    public async Task An_account_without_a_credential_is_refused_over_HTTP_and_MCP_before_any_run_starts()
    {
        var server = new ScriptedRuntime(Runs.DraftReply);
        await using var s = await TestServer.StartAsync(server, accounts: [new("bare")]);
        var key = s.Keys.Create("bare", null).Key;
        using var bare = s.As(key);

        var refused = await Post(bare, Runs.DraftRequest());

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var body = await Body(refused);
        Assert.Equal("model_credential_missing", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("allow_server_credential", body.GetProperty("errors")[0].GetString(), StringComparison.Ordinal);
        Assert.Empty(server.IntakePrompts);
        Assert.Empty(await s.Log.ListAsync(new RunListQuery(null, null, 10, null), CancellationToken.None));

        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(s.BaseAddress, "/v1/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" },
        }));
        var arguments = JsonSerializer.SerializeToElement(Runs.DraftRequest(), ContractJson.Options).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        var tool = await client.CallToolAsync(OrchestrateTool.Name, arguments);
        Assert.True(tool.IsError);
        Assert.Contains("model_credential_missing:", ((TextContentBlock)tool.Content[0]).Text, StringComparison.Ordinal);
        // The owner's key is not an account's.
        Assert.Equal(HttpStatusCode.OK, (await Post(s.Http, Runs.DraftRequest())).StatusCode);
    }

    /// <summary>Three runtimes stand for the server's credential, alice's key and bob's key; each run must land on exactly its own.</summary>
    [Fact]
    public async Task A_read_only_run_goes_to_its_accounts_runtime_and_never_another()
    {
        var (server, alice, bob) = (new ScriptedRuntime(Runs.DraftReply), new ScriptedRuntime(Runs.DraftReply), new ScriptedRuntime(Runs.DraftReply));
        AccountSettings[] accounts = [Own("alice"), Own("bob", "bob-key"), new("shared", AllowServerCredential: true)];
        var credentials = new AccountCredentials(accounts);
        IWorkerRuntime For(string? actor) => credentials.For(actor) is { ApiKeySecret: "alice-key" } ? alice : credentials.For(actor) is not null ? bob : server;
        await using var s = await TestServer.StartAsync(server, accounts: accounts, runtimeFor: For);

        foreach (var who in new[] { "alice", "bob", "shared" })
        {
            using var client = s.As(s.Keys.Create(who, null).Key);
            Assert.Equal(HttpStatusCode.OK, (await Post(client, Runs.DraftRequest())).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await Post(s.Http, Runs.DraftRequest())).StatusCode);

        Assert.Equal((1, 1, 2), (alice.IntakePrompts.Count, bob.IntakePrompts.Count, server.IntakePrompts.Count));
        Assert.Equal((1, 1, 2), (alice.Created.Count, bob.Created.Count, server.Created.Count));
    }

    [Fact]
    public async Task A_sessions_research_call_runs_on_the_account_that_started_the_batch()
    {
        var (server, alice) = (new ScriptedRuntime(Runs.WorkerReply), new ScriptedRuntime(Runs.WorkerReply));
        AccountSettings[] accounts = [Own("alice")];
        var credentials = new AccountCredentials(accounts);
        await using var s = await TestServer.StartAsync(server, accounts: accounts, runtimeFor: actor => credentials.For(actor) is null ? server : alice);
        var repo = Runs.GitRepo(s.WorkerRoot);
        var token = s.Tokens.Issue(new RunTokenClaims("run-task", repo.Path, repo.Commit, null, null, DateTimeOffset.UtcNow.AddHours(1), "alice"), DateTimeOffset.UtcNow);
        var owners = s.Tokens.Issue(new RunTokenClaims("run-owner", repo.Path, repo.Commit, null, null, DateTimeOffset.UtcNow.AddHours(1)), DateTimeOffset.UtcNow);
        var question = new RunRequest("request/v1", "What does the README say?", new RequestContext(false, "default", Repository: repo));

        Assert.Equal("alice", (await Body(await Post(s.As(token), question))).GetProperty("actor").GetString());
        Assert.Equal("owner", (await Body(await Post(s.As(owners), question))).GetProperty("actor").GetString());

        Assert.Equal((1, 1), (alice.IntakePrompts.Count, server.IntakePrompts.Count));
    }

    [Fact]
    public async Task A_run_started_before_its_account_was_removed_fails_with_a_credential_error_and_spends_nothing()
    {
        var server = new ScriptedRuntime(Runs.DraftReply);
        var credentials = new AccountCredentials([]);
        await using var s = await TestServer.StartAsync(server, runtimeFor: actor => credentials.For(actor) is null ? server : throw new InvalidOperationException());
        var repo = Runs.GitRepo(s.WorkerRoot);
        var token = s.Tokens.Issue(new RunTokenClaims("run-task", repo.Path, repo.Commit, null, null, DateTimeOffset.UtcNow.AddHours(1), "gone"), DateTimeOffset.UtcNow);

        var result = await Body(await Post(s.As(token), new RunRequest("request/v1", "What does the README say?", new RequestContext(false, "default", Repository: repo))));

        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.Equal("credential_unavailable", result.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(server.IntakePrompts);
    }

    [Fact]
    public async Task Each_accounts_claude_code_runtime_sees_its_own_key_and_never_another()
    {
        using var dir = new TempDir();
        var claude = FakeClaude(dir);
        var server = await ClaudeCodeWorkerRuntime.ConnectAsync(claude, "2.1.195", new ClaudeCodeCredential("sk-server", false), CancellationToken.None);
        AccountSettings[] accounts = [Own("alice", "alice-key"), Own("bob", "bob-key", "https://gateway.example.com/anthropic/"), new("shared", AllowServerCredential: true), new("bare")];
        var runtimes = new AccountRuntimes(server, new AccountCredentials(accounts), item => $"sk-{item}");
        var calls = Path.Combine(dir.Path, "calls.txt");

        foreach (var who in new[] { "alice", "bob", "shared", null, "owner" })
            Assert.Equal("hi", await runtimes.For(who).GenerateAsync(null, "hello", CancellationToken.None));

        Assert.Equal(["api=sk-alice-key oauth= base=", "api=sk-bob-key oauth= base=https://gateway.example.com/anthropic", "api=sk-server oauth= base=", "api=sk-server oauth= base=", "api=sk-server oauth= base="],
            File.ReadAllLines(calls));
        Assert.Same(runtimes.For("alice"), runtimes.For("alice"));
        Assert.NotSame(runtimes.For("alice"), runtimes.For("bob"));
        Assert.Equal(ErrorCode.CredentialUnavailable, Assert.Throws<ChargehandException>(() => runtimes.For("bare")).Code);
    }

    [Fact]
    public async Task An_accounts_runtime_keeps_its_own_sessions_and_a_subscription_login_never_reaches_it()
    {
        using var dir = new TempDir();
        var claude = FakeClaude(dir);
        // The server runs on a subscription token; the account's runtime must not inherit it.
        var server = await ClaudeCodeWorkerRuntime.ConnectAsync(claude, "2.1.195", new ClaudeCodeCredential("oauth-server", true), CancellationToken.None);
        var runtimes = new AccountRuntimes(server, new AccountCredentials([Own("alice", "alice-key")]), item => $"sk-{item}");

        await runtimes.For("alice").GenerateAsync(null, "hello", CancellationToken.None);
        await runtimes.For(null).GenerateAsync(null, "hello", CancellationToken.None);

        Assert.Equal(["api=sk-alice-key oauth= base=", "api= oauth=oauth-server base="], File.ReadAllLines(Path.Combine(dir.Path, "calls.txt")));
        var aliceSession = await runtimes.For("alice").CreateAsync(new NodeSpec(dir.Path, "build", null, [], new Dictionary<string, string>()), CancellationToken.None);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => server.SetInstructionAsync(aliceSession.Id, "k", "v", CancellationToken.None));
    }

    [Fact]
    public async Task A_secret_that_does_not_resolve_is_a_credential_error_naming_the_account()
    {
        using var dir = new TempDir();
        var server = await ClaudeCodeWorkerRuntime.ConnectAsync(FakeClaude(dir), "2.1.195", new ClaudeCodeCredential("sk-server", false), CancellationToken.None);
        var runtimes = new AccountRuntimes(server, new AccountCredentials([Own("alice", "missing-item")]), item => throw new InvalidOperationException($"no secret source resolved '{item}'"));

        var e = Assert.Throws<ChargehandException>(() => runtimes.For("alice"));

        Assert.Equal(ErrorCode.CredentialUnavailable, e.Code);
        Assert.Contains("missing-item", e.Message, StringComparison.Ordinal);
        Assert.Contains("alice", e.Action, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenCode_cannot_carry_an_accounts_key_and_the_server_says_so_before_it_starts()
    {
        var opencode = new ScriptedRuntime(Runs.DraftReply);
        Assert.Contains("claude_code", AccountRuntimes.Unsupported(opencode, [Own("alice")])!, StringComparison.Ordinal);
        Assert.Null(AccountRuntimes.Unsupported(opencode, [new("shared", AllowServerCredential: true)]));
        Assert.Null(AccountRuntimes.Unsupported(opencode, null));
        using var dir = new TempDir();
        Assert.Null(AccountRuntimes.Unsupported(await ClaudeCodeWorkerRuntime.ConnectAsync(FakeClaude(dir), "2.1.195", new ClaudeCodeCredential("k", false), CancellationToken.None), [Own("alice")]));
        // Defence in depth: asked for an account's runtime, an OpenCode server refuses rather than lend its own credential.
        var e = Assert.Throws<ChargehandException>(() => new AccountRuntimes(opencode, new AccountCredentials([Own("alice")]), item => item).For("alice"));
        Assert.Equal(ErrorCode.CredentialUnavailable, e.Code);
    }

    /// <summary>A stand-in CLI that answers every call with "hi" and appends the credential variables it was started with to <c>calls.txt</c>.</summary>
    private static string FakeClaude(TempDir dir)
    {
        var script = dir.Write("claude", """
            #!/bin/sh
            if [ "$1" = "--version" ]; then echo "2.1.195 (Claude Code)"; exit 0; fi
            if [ "$1" = "auth" ]; then exit 0; fi
            cat > /dev/null
            echo "api=$ANTHROPIC_API_KEY oauth=$CLAUDE_CODE_OAUTH_TOKEN base=$ANTHROPIC_BASE_URL" >> "$(dirname "$0")/calls.txt"
            echo '{"is_error":false,"result":"hi"}'
            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    // ---- batches ----

    private static RunRequest Batch(decimal? usd = 5m) =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([new DrivenTask("t1", Goal: "Add a retry")], MaxTokensTotal: 10_000_000, MaxUsdTotal: usd));

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private static JsonElement DeliveryOf(ResultContract result) => JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "driven-batch").Content!).RootElement.GetProperty("credential_delivery");

    [Fact]
    public async Task A_batch_of_an_account_with_its_own_key_runs_on_that_key_priced_and_in_the_environment()
    {
        // The server is on a subscription token with a gateway; the account's key must go to neither.
        using var rig = new DrivenRunTests.Rig(mcpForward: "chargehand-host:4300", modelUrl: "http://gateway-host:4001/anthropic", accounts: [Own("alice")]);

        var result = await rig.Run.RunAsync(Batch(), "run-batch-a", Token, default, actor: "alice");

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal("environment", DeliveryOf(result).GetString());
        Assert.Equal("alice", result.Actor);
        Assert.NotNull(result.Usage.Usd);
        var spec = Assert.Single(rig.Engine.Started);
        Assert.Equal("value-of-alice-key", spec.Env["ANTHROPIC_API_KEY"]);
        Assert.DoesNotContain("CLAUDE_CODE_OAUTH_TOKEN", spec.Env.Keys);
        Assert.DoesNotContain("ANTHROPIC_BASE_URL", spec.Env.Keys);
        Assert.Null(Assert.Single(rig.Engine.Egresses).Gateway);
        // Only the chargehand forward: the server's model gateway is not on this batch's egress.
        Assert.Contains("egress run-batch-a [4300=chargehand-host:4300]", rig.Engine.Calls);
        Assert.DoesNotContain(rig.Engine.Egresses.SelectMany(e => e.Forwards ?? []), f => f.Contains("gateway-host", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_batch_of_an_account_with_a_gateway_key_goes_through_its_gateway_and_not_the_servers()
    {
        using var rig = new DrivenRunTests.Rig(mcpForward: "chargehand-host:4300", modelUrl: "http://server-gateway:4001/anthropic",
            accounts: [Own("alice", "alice-gw-key", "http://alice-gateway:4002/anthropic")]);

        var result = await rig.Run.RunAsync(Batch(), "run-batch-g", Token, default, actor: "alice");

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal("gateway_key", DeliveryOf(result).GetString());
        var spec = Assert.Single(rig.Engine.Started);
        Assert.Equal("http://chargehand-driven:4002/anthropic", spec.Env["ANTHROPIC_BASE_URL"]);
        Assert.Equal("value-of-alice-gw-key", spec.Env["ANTHROPIC_API_KEY"]);
        Assert.Contains("egress run-batch-g [4300=chargehand-host:4300 4002=alice-gateway:4002]", rig.Engine.Calls);
        Assert.DoesNotContain("server-gateway", string.Join('\n', rig.Engine.Calls), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_https_gateway_of_an_account_is_allowed_on_that_batchs_egress_only()
    {
        using var rig = new DrivenRunTests.Rig(accounts: [Own("alice", "alice-gw-key", "https://alice-gateway.example.com/anthropic"), Own("bob", "bob-key")]);

        await rig.Run.RunAsync(Batch(), "run-batch-h", Token, default, actor: "alice");
        await rig.Run.RunAsync(Batch(), "run-batch-i", Token, default, actor: "bob");

        Assert.Equal("https://alice-gateway.example.com/anthropic", rig.Engine.Started[0].Env["ANTHROPIC_BASE_URL"]);
        Assert.Contains("alice-gateway.example.com", rig.Engine.Egresses[0].Allow!);
        Assert.DoesNotContain("alice-gateway.example.com", rig.Engine.Egresses[1].Allow!);
        Assert.False(rig.Engine.Started[1].Env.ContainsKey("ANTHROPIC_BASE_URL"));
    }

    [Fact]
    public async Task Two_accounts_batches_never_hold_each_others_key_nor_the_servers()
    {
        using var rig = new DrivenRunTests.Rig(accounts: [Own("alice", "alice-key"), Own("bob", "bob-key")]);

        await rig.Run.RunAsync(Batch(), "run-batch-a", Token, default, actor: "alice");
        await rig.Run.RunAsync(Batch(), "run-batch-b", Token, default, actor: "bob");

        var (a, b) = (rig.Engine.Started[0], rig.Engine.Started[1]);
        Assert.Equal(("value-of-alice-key", "value-of-bob-key"), (a.Env["ANTHROPIC_API_KEY"], b.Env["ANTHROPIC_API_KEY"]));
        Assert.DoesNotContain(a.Env, kv => kv.Value.Contains("bob-key", StringComparison.Ordinal) || kv.Value.Contains("claude-code-oauth-token", StringComparison.Ordinal));
        Assert.DoesNotContain(b.Env, kv => kv.Value.Contains("alice-key", StringComparison.Ordinal) || kv.Value.Contains("claude-code-oauth-token", StringComparison.Ordinal));
        var calls = string.Join('\n', rig.Engine.Calls);
        Assert.DoesNotContain("alice-key", calls, StringComparison.Ordinal);
        Assert.DoesNotContain("bob-key", calls, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_batch_on_an_accounts_key_needs_a_dollar_cap_because_the_key_is_priced()
    {
        using var rig = new DrivenRunTests.Rig(accounts: [Own("alice")]);

        var result = await rig.Run.RunAsync(Batch(usd: null), "run-batch-c", Token, default, actor: "alice");

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal(ErrorCode.InvalidRequest, result.Error!.Code);
        Assert.Empty(rig.Engine.Started);
    }

    [Fact]
    public async Task A_batch_of_an_account_with_no_credential_fails_before_any_container_exists_and_the_owners_still_runs()
    {
        using var rig = new DrivenRunTests.Rig(mcpForward: "chargehand-host:4300", accounts: [new("bare", Driven: true)]);

        var refused = await rig.Run.RunAsync(Batch(), "run-batch-d", Token, default, actor: "bare");
        var owner = await rig.Run.RunAsync(Batch(), "run-batch-e", Token, default, actor: "owner");

        Assert.Equal(ErrorCode.CredentialUnavailable, refused.Error!.Code);
        Assert.Contains("allow_server_credential", refused.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Engine.Calls, c => c.Contains("run-batch-d", StringComparison.Ordinal));
        Assert.Equal("token_exchange", DeliveryOf(owner).GetString());
    }

    [Fact]
    public async Task An_account_allowed_the_servers_credential_gets_the_servers_delivery_unchanged()
    {
        using var rig = new DrivenRunTests.Rig(mcpForward: "chargehand-host:4300", accounts: [new("shared", Driven: true, AllowServerCredential: true)]);

        var result = await rig.Run.RunAsync(Batch(), "run-batch-f", Token, default, actor: "shared");

        Assert.Equal("token_exchange", DeliveryOf(result).GetString());
        Assert.Null(result.Usage.Usd);
        Assert.DoesNotContain(rig.Engine.Started.Single().Env, kv => kv.Value.Contains("value-of-claude-code-oauth-token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_resumed_batch_keeps_its_accounts_credential()
    {
        using var rig = new DrivenRunTests.Rig(durable: true, accounts: [Own("alice")]);
        var state = new BatchState("run-batch-r", DateTimeOffset.UtcNow, new string('0', 32), Batch(), null,
            [new TaskEntry("t1", null, "Add a retry")], [], null, "alice");

        var result = await rig.Run.RunAsync(Batch(), "run-batch-r", Token, default, resume: state);

        Assert.Equal("alice", result.Actor);
        Assert.Equal("value-of-alice-key", Assert.Single(rig.Engine.Started).Env["ANTHROPIC_API_KEY"]);
    }
}
