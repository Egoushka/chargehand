using System.Net;
using Chargehand.Config;
using Chargehand.Onboarding;

namespace Chargehand.Tests;

/// <summary>chargehand doctor: each check against a fake machine, and the exit code.</summary>
public class DoctorTests
{
    private const string Digest = "registry.example/session@sha256:0000000000000000000000000000000000000000000000000000000000000000";
    private const string Canary = "canary-secret-value-91ac";

    private sealed class FakeEnvironment(TempDir dir) : IDoctorEnvironment
    {
        public HashSet<string> Files { get; } = [];
        public HashSet<string> Directories { get; } = [];
        public HashSet<string> Secrets { get; } = [];
        public HashSet<string> OnPaths { get; } = ["claude"];
        public Dictionary<string, string> Variables { get; } = [];
        public Dictionary<string, IReadOnlyList<FoundRepository>> Repos { get; } = [];
        public ProbeOutcome Server { get; set; } = new(200);
        public ProbeOutcome Runner { get; set; } = new(200);
        public Profile? Loaded { get; set; }
        public Exception? LoadFailure { get; set; }

        public bool FileExists(string path) => Files.Contains(path);
        public bool DirectoryExists(string path) => Directories.Contains(path);
        public Profile LoadProfile(string path) => LoadFailure is null ? Loaded ?? new Profile("profile/v1") : throw LoadFailure;
        public bool SecretResolves(Profile profile, string item) => Secrets.Contains(item);
        public string? EnvironmentVariable(string name) => Variables.GetValueOrDefault(name);
        public bool OnPath(string binary) => OnPaths.Contains(binary);
        public IReadOnlyList<FoundRepository> Repositories(string root) => Repos.GetValueOrDefault(root) ?? [];
        public Task<ProbeOutcome> ProbeServerAsync(Profile profile, HttpSettings http, CancellationToken ct) => Task.FromResult(Server);
        public Task<ProbeOutcome> ProbeRunnerAsync(Profile profile, DrivenRunner runner, CancellationToken ct) => Task.FromResult(Runner);

        public Profile Parse(string json)
        {
            Loaded = Profile.Load(dir.Write($"p{Guid.NewGuid():N}.json", json));
            return Loaded;
        }
    }

    private static readonly string[] PresetAllow = ["api.anthropic.com", "registry.npmjs.org", "api.nuget.org", "pypi.org", "files.pythonhosted.org"];

    private static async Task<IReadOnlyList<CheckResult>> RunAsync(FakeEnvironment env, string json, string path = "profile.json")
    {
        env.Files.Add(path);
        env.Parse(json);
        return await Doctor.RunAsync(path, env, PresetAllow, default);
    }

    private static CheckResult Line(IEnumerable<CheckResult> results, string name) => Assert.Single(results, r => r.Name == name);

    private static FoundRepository Dotnet(string name = "api") => new(name, new RepositoryScan([new("dotnet", ["App.sln"], ["api.nuget.org", "globalcdn.nuget.org"])], []));

    private static readonly string Full = """
        {"schema":"profile/v1",
         "claude_code":{"version":"2.1.283","api_key_secret":"anthropic-key"},
         "repository_roots":["/srv/repos"],
         "http":{"api_key_secret":"chargehand-api-key","port":4300},
         "driven":{"enabled":true,"images":["@@DIGEST@@"],"push_secret":"push-token",
                   "runner":{"url":"http://runner.example:4310","api_key_secret":"runner-key"},
                   "network":{"allow":["globalcdn.nuget.org"]}}}
        """.Replace("@@DIGEST@@", Digest);

    private static FakeEnvironment Healthy(TempDir dir)
    {
        var env = new FakeEnvironment(dir);
        env.Secrets.UnionWith(["anthropic-key", "chargehand-api-key", "push-token", "runner-key"]);
        env.Directories.Add("/srv/repos");
        env.Repos["/srv/repos"] = [Dotnet()];
        return env;
    }

    [Fact]
    public async Task A_complete_setup_is_all_ok_and_exits_0()
    {
        using var dir = new TempDir();
        var results = await RunAsync(Healthy(dir), Full);

        Assert.All(results, r => Assert.Equal(CheckStatus.Ok, r.Status));
        Assert.Equal(0, Doctor.ExitCode(results));
        Assert.Equal(
            ["profile", "model credential", "server", "runner", "driven.images[0]", "secret chargehand-api-key", "secret push-token", "secret runner-key", "repository_roots", "egress"],
            results.Select(r => r.Name));
        Assert.Contains("pinned by digest", Line(results, "driven.images[0]").Message);
        Assert.Contains("1 repository", Line(results, "egress").Message);
    }

    [Fact]
    public async Task A_profile_that_does_not_load_is_one_failing_line_with_the_action_and_nothing_else_runs()
    {
        using var dir = new TempDir();
        var env = Healthy(dir);
        env.LoadFailure = new Chargehand.ChargehandException(Chargehand.Contracts.ErrorCode.InvalidRequest, "mcp_servers.x: set one of url and command", "Fix the entry in the profile.");
        env.Files.Add("p.json");

        var results = await Doctor.RunAsync("p.json", env, PresetAllow, default);

        var line = Assert.Single(results);
        Assert.Equal(CheckStatus.Fail, line.Status);
        Assert.Contains("set one of url and command", line.Message);
        Assert.Equal("Fix the entry in the profile.", line.Fix);
        Assert.Equal(1, Doctor.ExitCode(results));
    }

    [Fact]
    public async Task A_json_error_in_the_profile_fails_with_a_fix()
    {
        using var dir = new TempDir();
        var env = Healthy(dir);
        env.LoadFailure = new System.Text.Json.JsonException("bad token at line 3");
        env.Files.Add("p.json");

        var line = Assert.Single(await Doctor.RunAsync("p.json", env, PresetAllow, default));

        Assert.Equal(CheckStatus.Fail, line.Status);
        Assert.Contains("bad token", line.Message);
    }

    [Fact]
    public async Task A_missing_profile_file_is_a_warning_that_defaults_apply_and_does_not_fail_the_run()
    {
        using var dir = new TempDir();
        var env = new FakeEnvironment(dir);

        var results = await Doctor.RunAsync("nope.json", env, PresetAllow, default);

        var profile = Line(results, "profile");
        Assert.Equal(CheckStatus.Warn, profile.Status);
        Assert.Contains("run chargehand init", profile.Fix);
        Assert.Equal(0, Doctor.ExitCode(results));
    }

    [Fact]
    public async Task The_server_check_tells_a_good_key_from_a_refused_one_and_from_no_answer()
    {
        using var dir = new TempDir();
        var env = Healthy(dir);

        Assert.Equal(CheckStatus.Ok, Line(await RunAsync(env, Full), "server").Status);

        env.Server = new(401);
        var refused = Line(await RunAsync(env, Full), "server");
        Assert.Equal(CheckStatus.Fail, refused.Status);
        Assert.Contains("refuses the key", refused.Message);
        Assert.Contains("chargehand-api-key", refused.Fix);

        env.Server = new(null, "Connection refused");
        var down = Line(await RunAsync(env, Full), "server");
        Assert.Equal(CheckStatus.Fail, down.Status);
        Assert.Contains("http://127.0.0.1:4300", down.Message);
        Assert.Contains("Connection refused", down.Message);
        Assert.Contains("chargehand serve", down.Fix);

        env.Server = new(500);
        Assert.Contains("HTTP 500", Line(await RunAsync(env, Full), "server").Message);
    }

    [Fact]
    public async Task A_server_whose_key_item_is_missing_is_not_probed_and_the_secret_line_fails()
    {
        using var dir = new TempDir();
        var env = Healthy(dir);
        env.Secrets.Remove("chargehand-api-key");

        var results = await RunAsync(env, Full);

        Assert.Equal(CheckStatus.Warn, Line(results, "server").Status);
        var secret = Line(results, "secret chargehand-api-key");
        Assert.Equal(CheckStatus.Fail, secret.Status);
        Assert.Contains("CHARGEHAND_API_KEY", secret.Fix);
        Assert.Equal(1, Doctor.ExitCode(results));
    }

    [Fact]
    public async Task A_listen_address_of_all_interfaces_is_probed_on_loopback()
    {
        using var dir = new TempDir();
        var env = Healthy(dir);
        env.Server = new(null, "no route");

        var line = Line(await RunAsync(env, """{"schema":"profile/v1","claude_code":{"version":"1","api_key_secret":"anthropic-key"},"http":{"api_key_secret":"chargehand-api-key","listen":"0.0.0.0","port":4301}}"""), "server");

        Assert.Contains("http://127.0.0.1:4301", line.Message);
    }

    [Fact]
    public async Task The_runner_check_fails_when_it_does_not_answer()
    {
        using var dir = new TempDir();
        var env = Healthy(dir);
        env.Runner = new(null, "Name or service not known");

        var line = Line(await RunAsync(env, Full), "runner");

        Assert.Equal(CheckStatus.Fail, line.Status);
        Assert.Contains("http://runner.example:4310", line.Message);
        Assert.Contains("chargehand runner", line.Fix);
    }

    [Fact]
    public async Task A_session_image_must_be_pinned_by_digest()
    {
        using var dir = new TempDir();
        var env = Healthy(dir);

        var results = await RunAsync(env, Full.Replace(Digest, "registry.example/session:latest"));

        var image = Line(results, "driven.images[0]");
        Assert.Equal(CheckStatus.Fail, image.Status);
        Assert.Contains("registry.example/session:latest is not pinned", image.Message);
    }

    [Fact]
    public async Task Driven_enabled_without_an_image_fails_and_driven_off_does_not()
    {
        using var dir = new TempDir();
        var env = Healthy(dir);

        Assert.Equal(CheckStatus.Fail, Line(await RunAsync(env, """{"schema":"profile/v1","claude_code":{"version":"1","api_key_secret":"anthropic-key"},"driven":{"enabled":true}}"""), "driven.images").Status);
        Assert.Equal(CheckStatus.Ok, Line(await RunAsync(env, """{"schema":"profile/v1","claude_code":{"version":"1","api_key_secret":"anthropic-key"},"driven":{"enabled":false}}"""), "driven.images").Status);
    }

    [Fact]
    public async Task Secrets_are_reported_once_per_item_with_every_field_that_names_them()
    {
        using var dir = new TempDir();
        var env = Healthy(dir);
        env.Secrets.Add("notes-token");

        var results = await RunAsync(env, """
            {"schema":"profile/v1","claude_code":{"version":"1","api_key_secret":"anthropic-key"},
             "http":{"api_key_secret":"shared"},
             "telemetry":{"otlp_endpoint":"https://lf.example/api/public/otel/v1/traces","public_key_secret":"shared","secret_key_secret":"lf-secret"},
             "mcp_servers":{"notes":{"command":["x"],"env":{"T":"{secret:notes-token}"}}}}
            """);

        Assert.Equal(["secret shared", "secret lf-secret", "secret notes-token"], results.Where(r => r.Name.StartsWith("secret ", StringComparison.Ordinal)).Select(r => r.Name));
        Assert.Contains("http.api_key_secret, telemetry.public_key_secret", Line(results, "secret shared").Message);
        Assert.Contains("mcp_servers.notes", Line(results, "secret notes-token").Message);
        Assert.Equal(CheckStatus.Fail, Line(results, "secret shared").Status);
        Assert.Equal(CheckStatus.Fail, Line(results, "secret lf-secret").Status);
    }

    [Fact]
    public async Task A_missing_driven_secret_only_warns_while_driven_is_off()
    {
        using var dir = new TempDir();
        var env = Healthy(dir);
        env.Secrets.Remove("push-token");

        var results = await RunAsync(env, Full.Replace("\"enabled\":true", "\"enabled\":false"));

        Assert.Equal(CheckStatus.Warn, Line(results, "secret push-token").Status);
    }

    [Fact]
    public async Task A_repository_root_that_does_not_exist_fails_and_slash_allows_any()
    {
        using var dir = new TempDir();
        var env = Healthy(dir);

        var results = await RunAsync(env, Full.Replace("[\"/srv/repos\"]", "[\"/srv/repos\",\"/gone\",\"/\"]"));

        var roots = results.Where(r => r.Name == "repository_roots").ToList();
        Assert.Equal([CheckStatus.Ok, CheckStatus.Fail, CheckStatus.Ok], roots.Select(r => r.Status));
        Assert.Contains("/gone does not exist", roots[1].Message);
        Assert.Contains("/ allows any", roots[2].Message);
    }

    [Fact]
    public async Task Without_repository_roots_serve_allows_worker_root_only_which_is_a_warning()
    {
        using var dir = new TempDir();
        var env = Healthy(dir);

        var withServer = Line(await RunAsync(env, """{"schema":"profile/v1","claude_code":{"version":"1","api_key_secret":"anthropic-key"},"http":{"api_key_secret":"chargehand-api-key"}}"""), "repository_roots");
        var without = Line(await RunAsync(env, """{"schema":"profile/v1","claude_code":{"version":"1","api_key_secret":"anthropic-key"}}"""), "repository_roots");

        Assert.Equal(CheckStatus.Warn, withServer.Status);
        Assert.Equal(CheckStatus.Ok, without.Status);
    }

    [Fact]
    public async Task The_egress_allowlist_must_cover_the_package_hosts_of_the_repositories_under_the_roots()
    {
        using var dir = new TempDir();
        var env = Healthy(dir);
        env.Repos["/srv/repos"] = [Dotnet("api"), Dotnet("web"), new("tool", new RepositoryScan([new("rust", ["Cargo.toml"], ["crates.io", "static.crates.io"])], []))];

        var line = Line(await RunAsync(env, Full.Replace("\"globalcdn.nuget.org\"", "\"static.crates.io\"")), "egress");

        Assert.Equal(CheckStatus.Fail, line.Status);
        Assert.Contains("api needs globalcdn.nuget.org", line.Message);
        Assert.Contains("web needs globalcdn.nuget.org", line.Message);
        Assert.Contains("tool needs crates.io", line.Message);
        Assert.DoesNotContain("static.crates.io", line.Message);
        Assert.Equal("add globalcdn.nuget.org, crates.io to driven.network.allow", line.Fix);
    }

    [Fact]
    public async Task A_wildcard_entry_and_the_model_gateway_host_count_as_allowed()
    {
        using var dir = new TempDir();
        var env = Healthy(dir);
        env.Repos["/srv/repos"] = [Dotnet(), new("gw", new RepositoryScan([new("go", ["go.mod"], ["proxy.golang.org"])], []))];

        var line = Line(await RunAsync(env, Full.Replace("\"allow\":[\"globalcdn.nuget.org\"]", "\"allow\":[\"*.nuget.org\"],\"model_url\":\"https://proxy.golang.org/v1\"")), "egress");

        Assert.Equal(CheckStatus.Ok, line.Status);
    }

    [Fact]
    public async Task A_short_allowlist_only_warns_while_driven_is_off()
    {
        using var dir = new TempDir();
        var env = Healthy(dir);

        var results = await RunAsync(env, Full.Replace("\"enabled\":true", "\"enabled\":false").Replace("\"allow\":[\"globalcdn.nuget.org\"]", "\"allow\":[]"));

        Assert.Equal(CheckStatus.Warn, Line(results, "egress").Status);
        Assert.Equal(0, Doctor.ExitCode(results));
    }

    [Fact]
    public async Task Repositories_without_package_hosts_need_nothing()
    {
        using var dir = new TempDir();
        var env = Healthy(dir);
        env.Repos["/srv/repos"] = [new("plain", new RepositoryScan([], []))];

        var line = Line(await RunAsync(env, Full), "egress");

        Assert.Equal(CheckStatus.Ok, line.Status);
        Assert.Contains("no repository with a known ecosystem", line.Message);
    }

    [Fact]
    public async Task Claude_code_with_no_key_or_token_warns_that_the_cli_login_cannot_be_checked()
    {
        using var dir = new TempDir();
        var env = new FakeEnvironment(dir);

        var line = Line(await RunAsync(env, """{"schema":"profile/v1"}"""), "model credential");

        Assert.Equal(CheckStatus.Warn, line.Status);
        Assert.Contains("CLI's own login", line.Message);
    }

    [Theory]
    [InlineData("anthropic-api-key", "API key")]
    [InlineData("claude-code-oauth-token", "subscription token")]
    public async Task Claude_code_finds_the_credential_the_secret_sources_resolve(string item, string kind)
    {
        using var dir = new TempDir();
        var env = new FakeEnvironment(dir);
        env.Secrets.Add(item);

        var line = Line(await RunAsync(env, """{"schema":"profile/v1"}"""), "model credential");

        Assert.Equal(CheckStatus.Ok, line.Status);
        Assert.Contains(kind, line.Message);
    }

    [Fact]
    public async Task Both_a_key_and_a_token_in_the_environment_fail()
    {
        using var dir = new TempDir();
        var env = new FakeEnvironment(dir);
        env.Secrets.UnionWith(["anthropic-api-key", "claude-code-oauth-token"]);

        Assert.Equal(CheckStatus.Fail, Line(await RunAsync(env, """{"schema":"profile/v1"}"""), "model credential").Status);
    }

    [Fact]
    public async Task A_named_credential_item_that_does_not_resolve_fails_with_the_variable_to_set()
    {
        using var dir = new TempDir();
        var env = new FakeEnvironment(dir);

        var line = Line(await RunAsync(env, """{"schema":"profile/v1","claude_code":{"version":"1","oauth_token_secret":"my-sub-token"}}"""), "model credential");

        Assert.Equal(CheckStatus.Fail, line.Status);
        Assert.Contains("MY_SUB_TOKEN", line.Fix);
    }

    [Fact]
    public async Task OpenCode_with_its_own_server_looks_for_a_provider_variable_and_with_a_named_server_cannot_check()
    {
        using var dir = new TempDir();
        var env = new FakeEnvironment(dir);
        env.OnPaths.Clear();
        env.OnPaths.Add("opencode");

        var none = Line(await RunAsync(env, """{"schema":"profile/v1","runtime":"opencode"}"""), "model credential");
        env.Secrets.Add("openai-api-key");
        var set = Line(await RunAsync(env, """{"schema":"profile/v1","runtime":"opencode"}"""), "model credential");
        var named = Line(await RunAsync(env, """{"schema":"profile/v1","opencode":{"url":"http://oc.example:4096","password_secret":"oc-pass","version":"2.0.18"}}"""), "model credential");

        Assert.Equal(CheckStatus.Warn, none.Status);
        Assert.Equal(CheckStatus.Ok, set.Status);
        Assert.Contains("OPENAI_API_KEY", set.Message);
        Assert.Equal(CheckStatus.Warn, named.Status);
    }

    [Fact]
    public async Task No_agent_cli_or_two_of_them_fail_as_a_runtime_line()
    {
        using var dir = new TempDir();
        var env = new FakeEnvironment(dir);
        env.OnPaths.Clear();

        Assert.Equal(CheckStatus.Fail, Line(await RunAsync(env, """{"schema":"profile/v1"}"""), "runtime").Status);

        env.OnPaths.UnionWith(["claude", "opencode"]);
        var both = Line(await RunAsync(env, """{"schema":"profile/v1"}"""), "runtime");
        Assert.Equal(CheckStatus.Fail, both.Status);
        Assert.Contains("CHARGEHAND_RUNTIME", both.Fix);
    }

    [Fact]
    public async Task The_environment_variable_names_the_runtime()
    {
        using var dir = new TempDir();
        var env = new FakeEnvironment(dir);
        env.OnPaths.UnionWith(["claude", "opencode"]);
        env.Variables["CHARGEHAND_RUNTIME"] = "claude_code";

        var results = await RunAsync(env, """{"schema":"profile/v1"}""");

        Assert.DoesNotContain(results, r => r.Name == "runtime");
        Assert.Contains("claude_code", Line(results, "model credential").Message);
    }

    [Fact]
    public void A_warning_alone_exits_0_and_a_failure_exits_1()
    {
        Assert.Equal(0, Doctor.ExitCode([new(CheckStatus.Ok, "a", "x"), new(CheckStatus.Warn, "b", "y")]));
        Assert.Equal(1, Doctor.ExitCode([new(CheckStatus.Warn, "b", "y"), new(CheckStatus.Fail, "c", "z")]));
    }

    [Fact]
    public void A_line_reads_status_name_message_and_fix()
    {
        Assert.Equal("ok   profile: p.json loads", Doctor.Format(new(CheckStatus.Ok, "profile", "p.json loads")));
        Assert.Equal("warn server: not probed; fix: start it", Doctor.Format(new(CheckStatus.Warn, "server", "not probed", "start it")));
        Assert.Equal("fail secret x: missing; fix: set X", Doctor.Format(new(CheckStatus.Fail, "secret x", "missing", "set X")));
    }

    [Fact]
    public async Task No_line_holds_a_secret_value()
    {
        using var dir = new TempDir();
        var env = Healthy(dir);
        env.Variables["ANTHROPIC_API_KEY"] = Canary;

        var results = await RunAsync(env, Full);

        Assert.DoesNotContain(Canary, string.Join('\n', results.Select(Doctor.Format)));
    }

    [Fact]
    public async Task The_cli_prints_one_line_per_check_and_returns_the_exit_code()
    {
        using var dir = new TempDir();
        var env = Healthy(dir);
        env.Secrets.Remove("push-token");
        env.Files.Add("other.json");
        env.Parse(Full);
        var output = new StringWriter();

        var code = await DoctorCli.RunAsync(["--profile", "other.json"], "ignored.json", env, PresetAllow, output, TextWriter.Null, default);

        var lines = output.ToString().TrimEnd().Split('\n');
        Assert.Equal(1, code);
        Assert.Contains(lines, l => l.StartsWith("fail secret push-token: missing (driven.push_secret); fix: set PUSH_TOKEN", StringComparison.Ordinal));
        Assert.StartsWith("ok   profile: other.json loads and validates", lines[0]);
    }

    [Theory]
    [InlineData("--profile")]
    [InlineData("extra")]
    public async Task The_cli_rejects_a_stray_argument(string arg)
    {
        using var dir = new TempDir();
        var error = new StringWriter();

        var code = await DoctorCli.RunAsync([arg], "p.json", new FakeEnvironment(dir), PresetAllow, TextWriter.Null, error, default);

        Assert.Equal(2, code);
        Assert.Contains("usage: chargehand doctor", error.ToString());
    }
}
