using System.Diagnostics;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.RunLog;
using Chargehand.Runtime;

namespace Chargehand.Tests;

/// <summary>Profile is fully optional (ADR 0026): Load defaults an absent file, and secrets try an ordered list.</summary>
public class ProfileTests
{
    [Fact]
    public void Load_returns_defaults_when_the_file_is_absent()
    {
        using var dir = new TempDir();

        var profile = Profile.Load(System.IO.Path.Combine(dir.Path, "missing.json"));

        Assert.Equal("profile/v1", profile.Schema);
        Assert.Equal(Profile.DefaultWorkerRoot, profile.WorkerRoot);
        Assert.Equal("cheap", profile.DefaultPreset);
        Assert.Null(profile.IntakeModel);
        Assert.Null(profile.Prices);
        Assert.Null(profile.Secrets);
        Assert.Equal(1.00m, profile.RunCapUsd);
    }

    [Fact]
    public void Default_worker_root_is_outside_home()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(Profile.DefaultWorkerRoot.StartsWith(home, StringComparison.Ordinal));
    }

    [Fact]
    public void A_profile_file_may_omit_the_now_optional_fields()
    {
        using var dir = new TempDir();
        var path = dir.Write("local.json", """{"schema":"profile/v1","opencode":{"url":"http://127.0.0.1:1","password_secret":"p","version":"1"}}""");

        var profile = Profile.Load(path);

        Assert.Equal(Profile.DefaultWorkerRoot, profile.WorkerRoot);
        Assert.Equal("cheap", profile.DefaultPreset);
        Assert.Null(profile.IntakeModel);
    }

    [Fact]
    public void Runtime_blocks_lists_the_runtimes_the_profile_configures()
    {
        var opencode = new OpenCodeSettings("http://127.0.0.1:1", "pw", "2.0.18");

        Assert.Empty(new Profile("profile/v1").RuntimeBlocks);
        Assert.Equal([RuntimeKind.Opencode], new Profile("profile/v1", opencode).RuntimeBlocks);
        Assert.Equal([RuntimeKind.Opencode, RuntimeKind.ClaudeCode],
            new Profile("profile/v1", opencode, ClaudeCode: new ClaudeCodeSettings(ClaudeCodeSettings.PinnedVersion)).RuntimeBlocks);
    }

    [Fact]
    public void With_no_repository_roots_the_cli_allows_its_launch_directory()
    {
        var profile = new Profile("profile/v1").WithLaunchDirectory("/launch/dir");

        Assert.Equal([Profile.DefaultWorkerRoot, "/launch/dir"], profile.Roots);
    }

    [Fact]
    public void Explicit_repository_roots_win_over_the_launch_directory()
    {
        var profile = new Profile("profile/v1", RepositoryRoots: ["/repos"]).WithLaunchDirectory("/launch/dir");

        Assert.Equal(["/repos"], profile.Roots);
    }

    [Fact]
    public void An_unmapped_placeholder_model_is_unset_so_the_runtime_uses_its_default()
    {
        Assert.Null(new Profile("profile/v1").ResolveModel("provider/worker-model"));
    }

    [Fact]
    public void A_mapped_placeholder_resolves_and_a_real_model_id_passes_through()
    {
        var profile = new Profile("profile/v1", Models: new Dictionary<string, string> { ["provider/worker-model"] = "anthropic/sonnet" });

        Assert.Equal("anthropic/sonnet", profile.ResolveModel("provider/worker-model"));
        Assert.Equal("anthropic/haiku", profile.ResolveModel("anthropic/haiku"));
    }

    [Fact]
    public async Task With_no_models_map_the_worker_gets_no_model_and_the_chain_says_auto()
    {
        using var root = new TempDir();
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var orchestrator = new Orchestrator(Runs.Profile(root.Path) with { Models = null }, runtime, "2.0.16", Repo.Root,
            new JsonlRunLog(System.IO.Path.Combine(root.Path, "log.jsonl")), new Dictionary<string, int>());

        var r = await orchestrator.RunAsync(Runs.CheapRequest(Runs.GitRepo(root.Path)), CancellationToken.None);

        Assert.NotEqual(ResultStatus.Failed, r.Status);
        Assert.Null(Assert.Single(runtime.Created).Model);
        Assert.Equal("auto", r.PromptChain.AsSent.Model);
    }

    [Fact]
    public void With_no_secrets_configured_env_is_the_only_source()
    {
        var profile = new Profile("profile/v1");
        const string item = "profile-tests-only-env";
        var name = item.ToUpperInvariant().Replace('-', '_');
        Environment.SetEnvironmentVariable(name, "s3cr3t");
        try
        {
            Assert.Equal("s3cr3t", profile.Secret(item));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Secret_sources_are_tried_in_order_first_success_wins()
    {
        const string item = "profile-tests-command-item";
        var profile = new Profile("profile/v1", Secrets:
        [
            new SecretSource(Env: true),
            new SecretSource(Command: ["sh", "-c", "printf %s from-command-$0", "{item}"]),
        ]);

        // The env source is tried first and finds nothing (the var is unset), so the command source wins.
        Assert.Equal($"from-command-{item}", profile.Secret(item));
    }

    [Fact]
    public void Secret_command_runs_as_argv_no_shell()
    {
        var profile = new Profile("profile/v1", Secrets: [new SecretSource(Command: ["printf", "%s", "{item}-value"])]);

        Assert.Equal("db-password-value", profile.Secret("db-password"));
    }

    [Fact]
    public async Task Secret_command_gets_a_closed_stdin_not_the_parents()
    {
        // Under `chargehand mcp` the parent's stdin carries protocol bytes: a command that reads stdin must see EOF at once.
        var profile = new Profile("profile/v1", Secrets: [new SecretSource(Command: ["sh", "-c", "cat >/dev/null; printf %s read-to-eof"])]);

        var secret = Task.Run(() => profile.Secret("stdin-probe"));

        Assert.Same(secret, await Task.WhenAny(secret, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.Equal("read-to-eof", await secret);
    }

    [Fact]
    public void Secret_throws_when_every_source_fails()
    {
        var profile = new Profile("profile/v1", Secrets: [new SecretSource(Command: ["sh", "-c", "exit 1"])]);

        Assert.Throws<InvalidOperationException>(() => profile.Secret("whatever"));
    }

    [Fact]
    public void A_profile_without_mcp_servers_names_none()
    {
        using var dir = new TempDir();

        Assert.Null(Profile.Load(System.IO.Path.Combine(dir.Path, "missing.json")).McpServers);
        Assert.Null(Profile.Load(dir.Write("p.json", """{"schema":"profile/v1"}""")).McpServers);
    }

    [Fact]
    public void Mcp_servers_load_by_name()
    {
        using var dir = new TempDir();
        var profile = Profile.Load(dir.Write("p.json", """
            {"schema":"profile/v1","mcp_servers":{
              "memory-gateway":{"url":"https://mcp.example.internal/mcp","headers":{"Authorization":"Bearer {secret:memory-gateway-token}"}},
              "team-notes":{"command":["npx","-y","example-notes-mcp"],"env":{"NOTES_TOKEN":"{secret:team-notes-token}"}},
              "archive":{"url":"http://localhost:8080/sse","transport":"sse"}}}
            """));

        Assert.Equal("https://mcp.example.internal/mcp", profile.McpServers!["memory-gateway"].Url);
        Assert.Equal("Bearer {secret:memory-gateway-token}", profile.McpServers["memory-gateway"].Headers!["Authorization"]);
        Assert.Null(profile.McpServers["memory-gateway"].Transport);
        Assert.Equal(["npx", "-y", "example-notes-mcp"], profile.McpServers["team-notes"].Command);
        Assert.Equal("{secret:team-notes-token}", profile.McpServers["team-notes"].Env!["NOTES_TOKEN"]);
        Assert.Equal("sse", profile.McpServers["archive"].Transport);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("streamable-http")]
    [InlineData("sse")]
    public void Every_transport_of_a_url_server_loads(string transport)
    {
        using var dir = new TempDir();

        var profile = Profile.Load(dir.Write("p.json", """{"schema":"profile/v1","mcp_servers":{"gw":{"url":"http://localhost:8080/sse","transport":"TRANSPORT"}}}""".Replace("TRANSPORT", transport, StringComparison.Ordinal)));

        Assert.Equal(transport, profile.McpServers!["gw"].Transport);
    }

    [Theory]
    [InlineData("""{"mcp_servers":{"gw":{"url":"https://a.example.internal/mcp","command":["x"]}}}""")]      // both transports
    [InlineData("""{"mcp_servers":{"gw":{}}}""")]                                                             // neither
    [InlineData("""{"mcp_servers":{"gw":{"command":[]}}}""")]                                                 // an empty argv
    [InlineData("""{"mcp_servers":{"gw":{"url":"https://a.example.internal/mcp?key={secret:k}"}}}""")]      // a secret in a URL
    [InlineData("""{"mcp_servers":{"gw":{"command":["x","--key={secret:k}"]}}}""")]                        // a secret in argv
    [InlineData("""{"mcp_servers":{"gw":{"url":"not a url"}}}""")]                                            // not an http(s) URL
    [InlineData("""{"mcp_servers":{"gw":{"url":"ftp://a.example.internal/mcp"}}}""")]                       // not an http(s) URL
    [InlineData("""{"mcp_servers":{"Bad_Name":{"url":"https://a.example.internal/mcp"}}}""")]               // name pattern
    [InlineData("""{"mcp_servers":{"gw":{"command":["x"],"headers":{"a":"b"}}}}""")]                        // headers on stdio
    [InlineData("""{"mcp_servers":{"gw":{"url":"https://a.example.internal/mcp","env":{"a":"b"}}}}""")]    // env on a url server
    [InlineData("""{"mcp_servers":{"gw":{"command":["x"],"transport":"sse"}}}""")]                          // transport on stdio
    [InlineData("""{"mcp_servers":{"gw":{"url":"https://a.example.internal/mcp","transport":"pigeon"}}}""")] // unknown transport
    public void Invalid_mcp_servers_fail_at_load(string body)
    {
        using var dir = new TempDir();
        var path = dir.Write("p.json", body.Replace("{\"mcp_servers\"", "{\"schema\":\"profile/v1\",\"mcp_servers\"", StringComparison.Ordinal));

        var e = Assert.Throws<ChargehandException>(() => Profile.Load(path));

        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.False(string.IsNullOrEmpty(e.Action));
    }

    [Fact]
    public void An_invalid_server_error_names_the_server_and_never_a_secret()
    {
        using var dir = new TempDir();
        var path = dir.Write("p.json", """{"schema":"profile/v1","mcp_servers":{"team-docs":{"url":"https://a.example.internal/mcp?key={secret:k}"}}}""");

        var e = Assert.Throws<ChargehandException>(() => Profile.Load(path));

        Assert.Contains("team-docs", e.Message);
        Assert.DoesNotContain("a.example.internal", e.Message);
    }

    [Fact]
    public void A_command_source_that_hangs_falls_through_to_the_next_source()
    {
        var profile = new Profile("profile/v1", Secrets: [new SecretSource(Command: ["sleep", "30"]), new SecretSource(Command: ["echo", "from-second"])])
        {
            CommandTimeout = TimeSpan.FromMilliseconds(300),
        };

        Assert.Equal("from-second", profile.Secret("anything"));
    }

    [Fact]
    public void A_command_source_that_times_out_is_killed_and_the_error_says_so()
    {
        using var dir = new TempDir();
        var pidFile = System.IO.Path.Combine(dir.Path, "pid");
        var profile = new Profile("profile/v1", Secrets: [new SecretSource(Command: ["sh", "-c", $"echo $$ > {pidFile}; sleep 30"])])
        {
            CommandTimeout = TimeSpan.FromMilliseconds(500),
        };

        var e = Assert.Throws<InvalidOperationException>(() => profile.Secret("whatever"));

        Assert.Contains("'whatever'", e.Message);
        Assert.Contains("timed out", e.Message);
        var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(SpinWait.SpinUntil(() => !Alive(pid), TimeSpan.FromSeconds(5)), "the hung command outlived its timeout");
    }

    private static bool Alive(int pid)
    {
        try
        {
            return !Process.GetProcessById(pid).HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public void The_command_limit_defaults_to_fifteen_seconds() => Assert.Equal(TimeSpan.FromSeconds(15), new Profile("profile/v1").CommandTimeout);
}
