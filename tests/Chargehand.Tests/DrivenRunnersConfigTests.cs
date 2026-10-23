using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Runners.Api;
using Chargehand.Onboarding;

namespace Chargehand.Tests;

/// <summary>ADR 0058: <c>driven.runner</c> is one runner, <c>driven.runners</c> lists several, and a setting that cannot work is refused naming its key.</summary>
public class DrivenRunnersConfigTests
{
    private const string Session = "registry.example/session@sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string SessionArm = "registry.example/session@sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string Jdk = "registry.example/session-jdk@sha256:3333333333333333333333333333333333333333333333333333333333333333";

    private static DrivenSettings Load(string driven)
    {
        using var dir = new TempDir();
        return Profile.Load(dir.Write("p.json", $$$$"""{"schema":"profile/v1","driven":{{{{driven}}}}}""")).Driven!;
    }

    private static ChargehandException Refused(string driven) => Assert.Throws<ChargehandException>(() => Load(driven));

    [Fact]
    public void Runner_is_one_runner_named_default_with_every_other_setting_at_its_default()
    {
        var driven = Load("""{"runner":{"url":"http://runner:4310","api_key_secret":"runner-key"}}""");

        var runner = Assert.Single(driven.ResolvedRunners());
        Assert.Equal(new DrivenRunnerEntry("default", "http://runner:4310", "runner-key"), runner);
        Assert.Equal((null, "linux/amd64", 1d, null, WorkspaceMode.Shared, null), (runner.MaxSessions, runner.Platform, runner.Weight, runner.ReserveMb, runner.Workspace, runner.Forwards));
        Assert.False(driven.NamesRunners);
    }

    [Fact]
    public void Without_runner_or_runners_the_server_calls_the_engine_itself()
    {
        Assert.Empty(Load("""{"enabled":true}""").ResolvedRunners());
    }

    [Fact]
    public void Runners_keep_the_profiles_order_and_the_values_it_gives()
    {
        var driven = Load($$$"""
            {"images":["{{{Session}}}"],
             "runners":[
               {"name":"box","url":"http://runner:4310","api_key_secret":"box-key","max_sessions":4},
               {"name":"laptop-1","url":"http://laptop.example.net:4310","api_key_secret":"laptop-key","platform":"linux/arm64","max_sessions":2,"weight":0.5,"reserve_mb":4096,
                "workspace":"bundle","forwards":{"mcp":"server.example.net:4300","outside":"bridge"}}]}
            """);

        Assert.True(driven.NamesRunners);
        var (box, laptop) = (driven.ResolvedRunners()[0], driven.ResolvedRunners()[1]);
        Assert.Equal(new DrivenRunnerEntry("box", "http://runner:4310", "box-key", 4), box);
        Assert.Equal(new DrivenRunnerEntry("laptop-1", "http://laptop.example.net:4310", "laptop-key", 2, "linux/arm64", 0.5, 4096, WorkspaceMode.Bundle,
            new DrivenRunnerForwards(Mcp: "server.example.net:4300", Outside: "bridge")), laptop);
    }

    [Theory]
    [InlineData("""{"runner":{"url":"http://a:1","api_key_secret":"k"},"runners":[{"name":"b","url":"http://b:1","api_key_secret":"k"}]}""", "either runner or runners")]
    [InlineData("""{"runners":[]}""", "at least one runner")]
    [InlineData("""{"runners":[{"url":"http://a:1","api_key_secret":"k"}]}""", "runners[0]: name must be")]
    [InlineData("""{"runners":[{"name":"Box","url":"http://a:1","api_key_secret":"k"}]}""", "name must be 1 to 32 lowercase")]
    [InlineData("""{"runners":[{"name":"a","url":"http://a:1","api_key_secret":"k"},{"name":"a","url":"http://b:1","api_key_secret":"k"}]}""", "runners.a: name is used twice")]
    [InlineData("""{"runners":[{"name":"a","url":"http://a:1","api_key_secret":"k"},{"name":"b","url":"http://A:1/","api_key_secret":"k"}]}""", "runners.b: url is the same as runner 'a'")]
    [InlineData("""{"runners":[{"name":"a","url":"runner:4310","api_key_secret":"k"}]}""", "runners.a: url must be an absolute http or https URL")]
    [InlineData("""{"runners":[{"name":"a","url":"http://user:pw@a:1","api_key_secret":"k"}]}""", "carries credentials")]
    [InlineData("""{"runners":[{"name":"a","url":"http://a:1"}]}""", "runners.a: api_key_secret is required")]
    [InlineData("""{"runners":[{"name":"a","url":"http://a:1","api_key_secret":"k","max_sessions":0}]}""", "max_sessions must be at least 1")]
    [InlineData("""{"runners":[{"name":"a","url":"http://a:1","api_key_secret":"k","platform":"windows/amd64"}]}""", "platform must be one of linux/amd64, linux/arm64")]
    [InlineData("""{"runners":[{"name":"a","url":"http://a:1","api_key_secret":"k","weight":0}]}""", "weight must be above 0")]
    [InlineData("""{"runners":[{"name":"a","url":"http://a:1","api_key_secret":"k","weight":101}]}""", "weight must be above 0 and at most 100")]
    [InlineData("""{"runners":[{"name":"a","url":"http://a:1","api_key_secret":"k","reserve_mb":-1}]}""", "reserve_mb must be at least 0")]
    [InlineData("""{"runners":[{"name":"a","url":"http://a:1","api_key_secret":"k","forwards":{"mcp":"no-port"}}]}""", "forwards.mcp must be host:port")]
    [InlineData("""{"runners":[{"name":"a","url":"http://a:1","api_key_secret":"k","forwards":{"otlp_url":"https://c:4318"}}]}""", "forwards.otlp_url must be an http URL")]
    public void A_runner_setting_that_cannot_work_is_refused_naming_the_key(string driven, string problem)
    {
        var e = Refused(driven);

        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Contains("driven:", e.Message);
        Assert.Contains(problem, e.Message);
    }

    [Fact]
    public void Every_problem_is_listed_at_once()
    {
        var e = Refused("""{"runners":[{"name":"a","url":"http://a:1","api_key_secret":"k","max_sessions":0,"weight":0}]}""");

        Assert.Contains("max_sessions must be at least 1", e.Message);
        Assert.Contains("weight must be above 0", e.Message);
    }

    private static string Remote(string forwards = "") =>
        $$$"""{"name":"laptop","url":"http://l:1","api_key_secret":"k","workspace":"bundle"{{{forwards}}}}""";

    [Fact]
    public void A_remote_runner_must_say_where_the_server_is_when_the_profile_names_it_by_a_docker_network_name()
    {
        var mcp = Refused($$$"""{"network":{"mcp_forward":"chargehand:4300"},"runners":[{{{Remote()}}}]}""");
        Assert.Contains("runners.laptop: workspace is bundle", mcp.Message);
        Assert.Contains("forwards.mcp", mcp.Message);
        Assert.Contains("forwards.model_url", Refused($$$"""{"network":{"model_url":"http://litellm:4000/v1"},"runners":[{{{Remote()}}}]}""").Message);
        Assert.Contains("forwards.otlp_url", Refused($$$"""{"network":{"otlp_url":"http://collector:4318"},"runners":[{{{Remote()}}}]}""").Message);

        // An https gateway is the same address from anywhere, so it needs no override.
        Load($$$"""{"network":{"model_url":"https://gateway.example/v1"},"runners":[{{{Remote()}}}]}""");
        var driven = Load($$$"""
            {"network":{"mcp_forward":"chargehand:4300","model_url":"http://litellm:4000/v1","otlp_url":"http://collector:4318"},
             "runners":[{{{Remote(""","forwards":{"mcp":"server.example.net:4300","model_url":"http://server.example.net:4000/v1","otlp_url":"http://server.example.net:4318"}""")}}}]}
            """);
        Assert.Equal("server.example.net:4300", driven.ResolvedRunners().Single().Forwards!.Mcp);
    }

    [Fact]
    public void A_runner_on_the_servers_host_inherits_the_network_block_and_needs_no_forwards()
    {
        var driven = Load("""
            {"network":{"mcp_forward":"chargehand:4300","model_url":"http://litellm:4000/v1"},
             "runners":[{"name":"box","url":"http://runner:4310","api_key_secret":"k"}]}
            """);

        Assert.Null(driven.ResolvedRunners().Single().Forwards);
    }

    [Fact]
    public void An_image_variant_replaces_the_image_for_its_platform_only()
    {
        var driven = Load($$$"""
            {"images":["{{{Session}}}","{{{Jdk}}}"],"services":{"images":{"gradle":"{{{Jdk}}}"}},
             "image_variants":{"{{{Session}}}":{"linux/arm64":"{{{SessionArm}}}"} } }
            """);

        Assert.Equal(SessionArm, driven.ImageFor(Session, "linux/arm64"));
        Assert.Equal(Session, driven.ImageFor(Session, "linux/amd64"));
        Assert.Equal(Jdk, driven.ImageFor(Jdk, "linux/arm64"));          // no entry: used as written (a multi-platform digest)
    }

    [Theory]
    [InlineData("""{"images":["@@S@@"],"image_variants":{"registry.example/other@sha256:4444444444444444444444444444444444444444444444444444444444444444":{"linux/arm64":"@@A@@"}}}""", "must also be listed in driven.images")]
    [InlineData("""{"images":["@@S@@"],"image_variants":{"registry.example/session:latest":{"linux/arm64":"@@A@@"}}}""", "the key must be pinned by digest")]
    [InlineData("""{"images":["@@S@@"],"image_variants":{"@@S@@":{"linux/arm64":"registry.example/session:arm"}}}""", "linux/arm64: must be pinned by digest")]
    [InlineData("""{"images":["@@S@@"],"image_variants":{"@@S@@":{"linux/s390x":"@@A@@"}}}""", "platform 'linux/s390x' must be one of")]
    [InlineData("""{"images":["@@S@@"],"image_variants":{"@@S@@":{}}}""", "name at least one platform")]
    public void An_image_variant_that_cannot_work_is_refused(string driven, string problem)
    {
        var e = Refused(driven.Replace("@@S@@", Session).Replace("@@A@@", SessionArm));

        Assert.Contains("image_variants", e.Message);
        Assert.Contains(problem, e.Message);
    }

    // --- environment (ADR 0078)

    [Fact]
    public void The_environment_is_the_images_unless_the_profile_says_mise()
    {
        Assert.Null(Load("""{"enabled":true}""").Environment);
        Assert.False(Load("""{"enabled":true}""").UsesMise);
        Assert.False(Load("""{"environment":"image"}""").UsesMise);
        Assert.True(Load("""{"environment":"mise"}""").UsesMise);
    }

    [Theory]
    [InlineData("nix")]
    [InlineData("Mise")]
    [InlineData("")]
    public void An_environment_that_is_not_one_of_the_two_is_refused(string environment)
    {
        var e = Refused($$$"""{"environment":"{{{environment}}}"}""");
        Assert.Contains("driven.environment", e.Message);
        Assert.Contains("mise", e.Message);
    }

    [Theory]
    [InlineData("""{"schema":"profile/v1","driven":{"environment":"mise"}}""", true)]
    [InlineData("""{"schema":"profile/v1","driven":{"environment":"image"}}""", true)]
    [InlineData("""{"schema":"profile/v1","driven":{"environment":"nix"}}""", false)]
    public void The_schema_checks_the_environment(string profile, bool valid)
    {
        using var doc = JsonDocument.Parse(profile);
        Assert.Equal(valid, ConfigFileTests.ProfileSchema.Evaluate(doc.RootElement).IsValid);
    }

    // --- the schema

    [Theory]
    [InlineData("""{"schema":"profile/v1","driven":{"runners":[{"name":"box","url":"http://runner:4310","api_key_secret":"k"}]}}""", true)]
    [InlineData("""{"schema":"profile/v1","driven":{"runners":[{"name":"laptop","url":"http://l:4310","api_key_secret":"k","max_sessions":2,"platform":"linux/arm64","weight":0.5,"reserve_mb":4096,"workspace":"bundle","forwards":{"mcp":"s:4300","model_url":"http://s:4000/v1","otlp_url":"http://s:4318","outside":"bridge"}}]}}""", true)]
    [InlineData("""{"schema":"profile/v1","driven":{"runners":[]}}""", false)]
    [InlineData("""{"schema":"profile/v1","driven":{"runners":[{"name":"box","url":"http://runner:4310"}]}}""", false)]
    [InlineData("""{"schema":"profile/v1","driven":{"runners":[{"name":"Box","url":"http://runner:4310","api_key_secret":"k"}]}}""", false)]
    [InlineData("""{"schema":"profile/v1","driven":{"runners":[{"name":"box","url":"http://runner:4310","api_key_secret":"k","platform":"linux/riscv64"}]}}""", false)]
    [InlineData("""{"schema":"profile/v1","driven":{"runners":[{"name":"box","url":"http://runner:4310","api_key_secret":"k","weight":0}]}}""", false)]
    [InlineData("""{"schema":"profile/v1","driven":{"runners":[{"name":"box","url":"http://runner:4310","api_key_secret":"k","workspace":"nfs"}]}}""", false)]
    [InlineData("""{"schema":"profile/v1","driven":{"runners":[{"name":"box","url":"http://runner:4310","api_key_secret":"k","key":"inline-secret"}]}}""", false)] // no credential in the profile
    [InlineData("""{"schema":"profile/v1","driven":{"image_variants":{"registry.example/s@sha256:1111111111111111111111111111111111111111111111111111111111111111":{"linux/arm64":"registry.example/s@sha256:2222222222222222222222222222222222222222222222222222222222222222"}}}}""", true)]
    [InlineData("""{"schema":"profile/v1","driven":{"image_variants":{"registry.example/s@sha256:1111111111111111111111111111111111111111111111111111111111111111":{"linux/arm64":"registry.example/s:arm"}}}}""", false)]
    public void The_schema_checks_runners_and_image_variants(string profile, bool valid)
    {
        using var doc = JsonDocument.Parse(profile);
        Assert.Equal(valid, ConfigFileTests.ProfileSchema.Evaluate(doc.RootElement).IsValid);
    }

    // --- doctor

    private sealed class Machine(TempDir dir) : IDoctorEnvironment
    {
        public HashSet<string> Secrets { get; } = ["anthropic-key", "box-key", "laptop-key", "runner-key", "push-token", "chargehand-api-key"];
        public Dictionary<string, ProbeOutcome> Probes { get; } = [];
        public List<string> Probed { get; } = [];
        public Profile? Loaded { get; set; }

        public bool FileExists(string path) => true;
        public bool DirectoryExists(string path) => true;
        public Profile LoadProfile(string path) => Loaded!;
        public bool SecretResolves(Profile profile, string item) => Secrets.Contains(item);
        public string? EnvironmentVariable(string name) => null;
        public bool OnPath(string binary) => binary == "claude";
        public IReadOnlyList<FoundRepository> Repositories(string root) => [];
        public Task<ProbeOutcome> ProbeServerAsync(Profile profile, HttpSettings http, CancellationToken ct) => Task.FromResult(new ProbeOutcome(200));
        public Task<ProbeOutcome> ProbeRunnerAsync(Profile profile, DrivenRunner runner, CancellationToken ct)
        {
            Probed.Add(runner.Url);
            return Task.FromResult(Probes.GetValueOrDefault(runner.Url) ?? new ProbeOutcome(200));
        }

        public Profile Parse(string json) => Loaded = Profile.Load(dir.Write($"p{Guid.NewGuid():N}.json", json));
    }

    private static async Task<IReadOnlyList<CheckResult>> Check(Machine machine, string driven)
    {
        machine.Parse($$$"""{"schema":"profile/v1","claude_code":{"version":"2.1.283","api_key_secret":"anthropic-key"},"driven":{{{driven}}}}""");
        return await Doctor.RunAsync("profile.json", machine, [], default);
    }

    [Fact]
    public async Task Doctor_probes_every_runner_and_names_the_one_that_does_not_answer()
    {
        using var dir = new TempDir();
        var machine = new Machine(dir);
        machine.Probes["http://laptop.example.net:4310"] = new(null, "timed out");

        var results = await Check(machine, $$$"""
            {"enabled":true,"images":["{{{Session}}}"],"push_secret":"push-token",
             "runners":[{"name":"box","url":"http://runner:4310","api_key_secret":"box-key"},
                        {"name":"laptop","url":"http://laptop.example.net:4310","api_key_secret":"laptop-key","platform":"linux/arm64"}]}
            """);

        Assert.Equal(["http://runner:4310", "http://laptop.example.net:4310"], machine.Probed);
        Assert.Equal(CheckStatus.Ok, Assert.Single(results, r => r.Name == "runner box").Status);
        var laptop = Assert.Single(results, r => r.Name == "runner laptop");
        Assert.Equal(CheckStatus.Fail, laptop.Status);
        Assert.Contains("driven.runners.laptop.url", laptop.Fix);
        Assert.Contains("secret box-key", results.Select(r => r.Name));
        Assert.Contains("secret laptop-key", results.Select(r => r.Name));
        Assert.Contains("multi-platform", Assert.Single(results, r => r.Name == "runner laptop images").Message);
    }

    [Fact]
    public async Task Doctor_says_how_many_images_have_a_variant_for_a_runner_of_another_platform()
    {
        using var dir = new TempDir();

        var results = await Check(new Machine(dir), $$$"""
            {"enabled":true,"images":["{{{Session}}}"],"push_secret":"push-token",
             "image_variants":{"{{{Session}}}":{"linux/arm64":"{{{SessionArm}}}"}},
             "runners":[{"name":"laptop","url":"http://l:4310","api_key_secret":"laptop-key","platform":"linux/arm64"}]}
            """);

        Assert.Contains("1 of 1 driven.images have a variant", Assert.Single(results, r => r.Name == "runner laptop images").Message);
    }

    [Fact]
    public async Task A_profile_with_runner_is_checked_under_the_name_it_always_had()
    {
        using var dir = new TempDir();
        var machine = new Machine(dir);

        var results = await Check(machine, $$$"""{"enabled":true,"images":["{{{Session}}}"],"push_secret":"push-token","runner":{"url":"http://runner:4310","api_key_secret":"runner-key"}}""");

        Assert.Equal(CheckStatus.Ok, Assert.Single(results, r => r.Name == "runner").Status);
        Assert.DoesNotContain(results, r => r.Name.EndsWith("images", StringComparison.Ordinal));
    }
}
