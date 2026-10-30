using System.Diagnostics;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Kernel;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Tasks.Api;
using Chargehand.RunLog;
using Chargehand.Runtime;
using Chargehand.Runtimes.Acp.Api;
using Chargehand.Runtimes.ClaudeCode.Api;
using Chargehand.Runtimes.OpenCode.Api;

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
    public void The_acp_block_loads_an_agents_key_secret_and_gateway_and_whether_uncapped_runs_are_allowed()
    {
        using var dir = new TempDir();
        var path = dir.Write("local.json", """
            {"schema":"profile/v1","runtime":"acp:codex",
             "acp":{"allow_uncapped":true,"agents_file":"/etc/acp-agents.json","agents":{"codex":{"api_key_secret":"openai-key","base_url":"https://gateway.example/v1"},"gemini":{"api_key_secret":"gemini-key"}}}}
            """);

        var profile = Profile.Load(path);

        Assert.True(profile.Acp!.AllowUncapped);
        Assert.Equal("/etc/acp-agents.json", profile.Acp.AgentsFile);
        Assert.Equal(new AcpAgentCredential("openai-key", "https://gateway.example/v1"), profile.Acp.CredentialFor("codex"));
        Assert.Equal(new AcpAgentCredential("gemini-key"), profile.Acp.CredentialFor("gemini"));
        Assert.Null(profile.Acp.CredentialFor("copilot"));
        Assert.Empty(profile.RuntimeBlocks);
    }

    [Fact]
    public void Without_an_acp_block_uncapped_runs_are_not_allowed()
    {
        using var dir = new TempDir();

        Assert.Null(Profile.Load(dir.Write("local.json", """{"schema":"profile/v1"}""")).Acp);
        Assert.False(new AcpSettings().AllowUncapped);
    }

    [Fact]
    public void Driven_limits_load_and_override_only_the_keys_they_set()
    {
        using var dir = new TempDir();
        var path = dir.Write("local.json", """{"schema":"profile/v1","driven":{"enabled":true,"limits":{"max_minutes":90,"memory_mb":8192}}}""");
        var limits = Profile.Load(path).Driven!.Limits!;
        var merged = limits.Apply(new DrivenPreset(MaxMinutes: 45, MaxTurns: 80, MemoryMb: 4096, Cpus: 2));
        Assert.Equal((90, 80, 8192, 2d), (merged.MaxMinutes, merged.MaxTurns, merged.MemoryMb, merged.Cpus));
    }

    [Theory]
    [InlineData("""{"max_minutes":0}""")]
    [InlineData("""{"memory_mb":128}""")]
    [InlineData("""{"cpus":64}""")]
    [InlineData("""{"pids":4}""")]
    public void Driven_limits_outside_the_containers_bounds_are_refused(string limits)
    {
        using var dir = new TempDir();
        var path = dir.Write("local.json", $$$"""{"schema":"profile/v1","driven":{"limits":{{{limits}}}}}""");
        Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ChargehandException>(() => Profile.Load(path)).Code);
    }

    [Fact]
    public void The_profile_binds_the_limits_block_with_the_tasks_modules_source_generated_context() =>
        Assert.IsType<TasksSettingsJsonContext>(Profile.LoadJson.GetTypeInfo(typeof(DrivenLimits)).OriginatingResolver);

    [Fact]
    public void Governor_history_keeps_the_defaults_it_left_out_and_refuses_values_that_cannot_work()
    {
        using var dir = new TempDir();
        Assert.Null(Profile.Load(dir.Write("none.json", """{"schema":"profile/v1","driven":{"governor":{}}}""")).Driven!.Governor!.History);
        Assert.Equal(new DrivenGovernorHistory(), Profile.Load(dir.Write("empty.json", """{"schema":"profile/v1","driven":{"governor":{"history":{}}}}""")).Driven!.Governor!.History);
        Assert.Equal(new DrivenGovernorHistory(10, 30, 90),
            Profile.Load(dir.Write("set.json", """{"schema":"profile/v1","driven":{"governor":{"history":{"min_tasks":10,"window_days":30,"percentile":90}}}}""")).Driven!.Governor!.History);
        foreach (var bad in new[] { """{"min_tasks":0}""", """{"window_days":0}""", """{"percentile":0}""", """{"percentile":101}""" })
        {
            var path = dir.Write("bad.json", """{"schema":"profile/v1","driven":{"governor":{"history":BAD}}}""".Replace("BAD", bad, StringComparison.Ordinal));
            Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ChargehandException>(() => Profile.Load(path)).Code);
        }
    }

    [Fact]
    public void Driven_governor_loads_and_a_session_gets_its_size_class_budget_and_the_defaults_it_left_out()
    {
        using var dir = new TempDir();
        var path = dir.Write("local.json", """{"schema":"profile/v1","driven":{"governor":{"mode":"observe","budgets":{"S":1,"L":20},"repeat_calls":6}}}""");
        var governor = Profile.Load(path).Driven!.Governor!;
        Assert.Equal(new SessionGovernor(BudgetUsd: 20m, PriorUsd: 0.5m, SizeClass: "L", RepeatCalls: 6, BudgetSource: new BudgetSource("flat")), governor.ToSession("L", null, 0.5m));
        Assert.Equal(4m, governor.ToSession(null, null, 0).BudgetUsd);
        Assert.Equal(3m, governor.ToSession("S", 3m, 0).BudgetUsd);
        Assert.Equal(GovernorMode.Off, Profile.Load(dir.Write("off.json", """{"schema":"profile/v1","driven":{"governor":{"mode":"off"}}}""")).Driven!.Governor!.Mode);
        Assert.Equal(GovernorMode.Enforce, Profile.Load(dir.Write("enforce.json", """{"schema":"profile/v1","driven":{"governor":{"mode":"enforce"}}}""")).Driven!.Governor!.Mode);
    }

    [Theory]
    [InlineData("""{"repeat_calls":0}""")]
    [InlineData("""{"checkpoint_at":1.5}""")]
    [InlineData("""{"budgets":{"XL":3}}""")]
    [InlineData("""{"budgets":{"M":0}}""")]
    [InlineData("""{"wait_patterns":["("]}""")]
    public void A_driven_governor_setting_that_cannot_work_is_refused(string governor)
    {
        using var dir = new TempDir();
        var path = dir.Write("local.json", $$$"""{"schema":"profile/v1","driven":{"governor":{{{governor}}}}}""");
        Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ChargehandException>(() => Profile.Load(path)).Code);
    }

    [Fact]
    public void Driven_admission_loads_and_leaves_out_what_it_defaults()
    {
        using var dir = new TempDir();
        var admission = Profile.Load(dir.Write("local.json", """{"schema":"profile/v1","driven":{"admission":{"max_load_per_cpu":1.5}}}""")).Driven!.Admission!;
        Assert.Equal(new DrivenAdmission(1024, 1.5), admission);
        Assert.Equal(new DrivenAdmission(2048), Profile.Load(dir.Write("reserve.json", """{"schema":"profile/v1","driven":{"admission":{"reserve_mb":2048}}}""")).Driven!.Admission);
    }

    [Fact]
    public void Driven_admission_asks_for_1536_MiB_and_holds_starts_above_10_percent_memory_pressure_unless_the_profile_says_otherwise()
    {
        using var dir = new TempDir();
        var defaults = Profile.Load(dir.Write("local.json", """{"schema":"profile/v1","driven":{"admission":{}}}""")).Driven!.Admission!;
        Assert.Equal((1536, 10.0, 1024, null), (defaults.DefaultRequestMb, defaults.MaxMemoryPressure, defaults.ReserveMb, defaults.MaxLoadPerCpu));
        var set = Profile.Load(dir.Write("set.json", """{"schema":"profile/v1","driven":{"admission":{"default_request_mb":2048,"max_memory_pressure":25.5}}}""")).Driven!.Admission!;
        Assert.Equal((2048, 25.5), (set.DefaultRequestMb, set.MaxMemoryPressure));
    }

    [Theory]
    [InlineData("""{"reserve_mb":-1}""")]
    [InlineData("""{"max_load_per_cpu":0}""")]
    [InlineData("""{"default_request_mb":511}""")]
    [InlineData("""{"max_memory_pressure":0}""")]
    [InlineData("""{"max_memory_pressure":100.5}""")]
    public void A_driven_admission_setting_that_cannot_work_is_refused(string admission)
    {
        using var dir = new TempDir();
        var path = dir.Write("local.json", $$$"""{"schema":"profile/v1","driven":{"admission":{{{admission}}}}}""");
        Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ChargehandException>(() => Profile.Load(path)).Code);
    }

    [Fact]
    public void Driven_planning_is_off_unless_the_profile_turns_it_on_and_defaults_to_the_cheap_preset()
    {
        using var dir = new TempDir();
        Assert.Null(Profile.Load(dir.Write("none.json", """{"schema":"profile/v1","driven":{}}""")).Driven!.Planning);
        Assert.Equal(new DrivenPlanning(true, "cheap"), Profile.Load(dir.Write("on.json", """{"schema":"profile/v1","driven":{"planning":{"enabled":true}}}""")).Driven!.Planning);
        Assert.Equal(new DrivenPlanning(false, "review"), Profile.Load(dir.Write("preset.json", """{"schema":"profile/v1","driven":{"planning":{"preset":"review"}}}""")).Driven!.Planning);
        var empty = dir.Write("empty.json", """{"schema":"profile/v1","driven":{"planning":{"preset":" "}}}""");
        Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ChargehandException>(() => Profile.Load(empty)).Code);
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
    public void A_placeholder_model_is_priced_by_the_id_it_maps_to()
    {
        var price = new ModelPrice(2m, 10m, 0.2m, 4m);
        var profile = new Profile("profile/v1", Models: new Dictionary<string, string> { ["provider/worker-model"] = "anthropic/sonnet" },
            Prices: new Dictionary<string, ModelPrice> { ["anthropic/sonnet"] = price });

        Assert.Equal(price, profile.PriceOf("provider/worker-model"));
        Assert.Equal(price, profile.PriceOf("anthropic/sonnet"));
        Assert.Null(profile.PriceOf("provider/small-model"));
        Assert.Null(new Profile("profile/v1").PriceOf("anthropic/sonnet"));
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
        // The next source is env, not a second command: the timeout is per command, so a fallback command would get the same 300 ms and fail
        // whenever a loaded machine is slow to start it.
        var item = $"profile-tests-after-a-hang-{Guid.NewGuid():N}";
        var name = item.ToUpperInvariant().Replace('-', '_');
        var profile = new Profile("profile/v1", Secrets: [new SecretSource(Command: ["sleep", "30"]), new SecretSource(Env: true)])
        {
            CommandTimeout = TimeSpan.FromMilliseconds(300),
        };
        Environment.SetEnvironmentVariable(name, "from-second");
        try
        {
            Assert.Equal("from-second", profile.Secret(item));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
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
