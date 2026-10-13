using Chargehand.Config;
using Chargehand.Containers;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;
using Chargehand.Workspace;

namespace Chargehand.Tests;

/// <summary>ADR 0048: the session's environment note, the cache volumes, the test services of a batch and the session image chosen by ecosystem. Containers are fakes.</summary>
public class SharedServicesTests
{
    private const string Digest = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string Image = $"registry.example/session@sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string JdkImage = $"registry.example/session-jdk@sha256:3333333333333333333333333333333333333333333333333333333333333333";
    private const string PostgresImage = $"docker.io/library/postgres@sha256:4444444444444444444444444444444444444444444444444444444444444444";
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";

    private static ToolchainManifest Manifest() => new(new Dictionary<string, string> { ["dotnet"] = "10.0.401", ["node"] = "v26.10.0", ["git"] = "2.43.0" }, ["java", "gradle", "docker"]);

    // --- the environment note

    [Fact]
    public void The_note_says_what_is_installed_what_is_not_how_the_tests_run_and_where_the_caches_and_services_are()
    {
        var note = EnvironmentNote.Render(Manifest(), new SessionEnvironment(["dotnet", "node"], ["dotnet", "test", "--nologo"], "detected", ["nuget"],
            [new EnvironmentService("postgres", "postgres:5432", "user app, database app")]));

        Assert.Contains("Installed: dotnet 10.0.401, node v26.10.0, git 2.43.0.", note);
        Assert.Contains("Not installed: java, gradle, docker.", note);
        Assert.Contains("only package registries on an allowlist answer", note);
        Assert.Contains("This repository uses: dotnet, node.", note);
        Assert.Contains("runs `dotnet test --nologo` on your branch", note);
        Assert.Contains("detected from the repository", note);
        Assert.Contains("nuget in /cache/nuget", note);
        Assert.Contains("postgres at postgres:5432: user app, database app", note);
        Assert.DoesNotContain("Package caches are empty", note);
    }

    [Fact]
    public void A_command_the_task_named_is_called_that_and_a_repository_with_none_is_told_the_branch_will_not_be_pushed_untested()
    {
        var named = EnvironmentNote.Render(Manifest(), new SessionEnvironment(["node"], ["sh", "scripts/check.sh"], "request"));
        Assert.Contains("the command the task named", named);
        Assert.Contains("Package caches are empty and belong to this task alone", named);
        Assert.DoesNotContain("Test services", named);

        var none = EnvironmentNote.Render(Manifest(), new SessionEnvironment([]));
        Assert.Contains("found no test command", none);
        Assert.DoesNotContain("This repository uses", none);
    }

    [Fact]
    public void Without_the_servers_facts_the_note_holds_the_image_alone()
    {
        var note = EnvironmentNote.Render(Manifest(), null);
        Assert.Contains("Installed: dotnet", note);
        Assert.DoesNotContain("This repository uses", note);
        Assert.DoesNotContain("chargehand runs", note);
    }

    [Fact]
    public async Task A_probe_keeps_the_version_of_a_tool_that_answers_and_lists_the_ones_that_do_not()
    {
        var manifest = await ToolchainManifest.ProbeAsync((tool, _, _) => Task.FromResult<string?>(tool switch
        {
            "dotnet" => "10.0.401\n",
            "node" => "v26.10.0\n",
            "python3" => "Python 3.12.3\n",
            "git" => "git version 2.43.0\n",
            "java" => "openjdk version \"21.0.4\" 2024-07-16\nOpenJDK Runtime Environment\n",
            "gradle" => "\n------------------------------------------------------------\nGradle 9.8.1\n------------------------------------------------------------\n",
            "pip3" => "pip 24.0 from /usr/lib/python3/dist-packages/pip (python 3.12)\n",
            _ => null,
        }), default);

        Assert.Equal("10.0.401", manifest.Tools["dotnet"]);
        Assert.Equal("v26.10.0", manifest.Tools["node"]);
        Assert.Equal("3.12.3", manifest.Tools["python3"]);
        Assert.Equal("2.43.0", manifest.Tools["git"]);
        Assert.Equal("21.0.4", manifest.Tools["java"]);
        Assert.Equal("9.8.1", manifest.Tools["gradle"]);
        Assert.Equal("24.0", manifest.Tools["pip3"]);
        Assert.Equal(["npm", "mvn", "go", "cargo", "docker"], manifest.Absent);
    }

    [Fact]
    public void The_manifest_survives_a_file_and_a_missing_or_broken_file_is_none()
    {
        using var dir = new TempDir();
        var file = dir.Write("environment.json", Manifest().ToJson());
        var read = ToolchainManifest.Load(file)!;
        Assert.Equal(Manifest().Tools, read.Tools);
        Assert.Equal(Manifest().Absent, read.Absent);
        Assert.Null(ToolchainManifest.Load(Path.Combine(dir.Path, "none.json")));
        Assert.Null(ToolchainManifest.Load(dir.Write("broken.json", "{not json")));
    }

    [Fact]
    public async Task The_session_verb_probes_the_machine_into_a_file_the_note_reads_back()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "environment.json");
        var code = await SessionCli.RunAsync(["--probe", file], TextWriter.Null, default);
        Assert.Equal(0, code);
        var manifest = ToolchainManifest.Load(file)!;
        Assert.True(manifest.Tools.ContainsKey("dotnet"), "the test host has the .NET SDK");
        Assert.Equal(ToolchainManifest.Probes.Count, manifest.Tools.Count + manifest.Absent.Count);
    }

    [Fact]
    public void The_environment_travels_in_the_task_file_and_is_left_out_when_there_is_none()
    {
        var environment = new SessionEnvironment(["gradle"], ["./gradlew", "test"], "detected", ["gradle"], [new EnvironmentService("postgres", "postgres:5432")]);
        var json = System.Text.Json.JsonSerializer.Serialize(new SessionTask("run1", "g", "chargehand/run1", 80, 45, 600, Environment: environment), SessionCli.Json);
        Assert.Contains("\"environment\":{\"ecosystems\":[\"gradle\"],\"verify\":[\"./gradlew\",\"test\"],\"verify_source\":\"detected\",\"caches\":[\"gradle\"],\"services\":[{\"name\":\"postgres\",\"address\":\"postgres:5432\"", json);
        using var dir = new TempDir();
        Assert.Equal(environment.Services, SessionCli.ReadTask(dir.Write("task.json", json)).Environment!.Services);
        Assert.DoesNotContain("environment", System.Text.Json.JsonSerializer.Serialize(new SessionTask("run1", "g", "chargehand/run1", 80, 45, 600), SessionCli.Json));
    }

    // --- cache volumes

    [Fact]
    public void A_cache_volume_belongs_to_one_ecosystem_of_one_repository()
    {
        var nuget = CacheVolumes.Name("nuget", "Owner/Repo");
        Assert.Equal(nuget, CacheVolumes.Name("nuget", "owner/repo"));
        Assert.Matches(@"^chargehand-cache-nuget-[0-9a-f]{12}$", nuget);
        Assert.NotEqual(nuget, CacheVolumes.Name("npm", "owner/repo"));
        Assert.NotEqual(nuget, CacheVolumes.Name("nuget", "owner/other"));
        Assert.Equal("nuget", CacheVolumes.EcosystemOf(nuget));
        Assert.Null(CacheVolumes.EcosystemOf("chargehand-work-run1"));
        Assert.Null(CacheVolumes.EcosystemOf("chargehand-cache-ruby-0123456789ab"));
        Assert.Throws<ArgumentException>(() => CacheVolumes.Name("ruby", "o/r"));
        Assert.Equal("owner/repo", SharedServicesPlan.Identity("git@github.com:owner/repo.git", "/x"));
        Assert.Equal("owner/repo", SharedServicesPlan.Identity("https://github.com/owner/repo", "/x"));
        Assert.Equal("/x", SharedServicesPlan.Identity("/local/path", "/x"));
    }

    private static ContainerSpec Spec(IReadOnlyList<string>? caches = null, IReadOnlyDictionary<string, string>? env = null) =>
        new("run-1", $"registry.example/session@sha256:{Digest}", "chargehand-work-run-1", "chargehand-out-run-1", "net-batch-1",
            env ?? new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://egress:3128" }, 4096, 2, 512, ["session"], Caches: caches);

    [Fact]
    public void A_session_mounts_each_cache_at_a_fixed_path_and_nothing_else_changes()
    {
        var nuget = CacheVolumes.Name("nuget", "o/r");
        var npm = CacheVolumes.Name("npm", "o/r");
        var args = ContainerTemplate.RunArgs(Spec([nuget, npm]), "/tmp/env");
        Assert.Equal(["-v", "chargehand-work-run-1:/work", "-v", "chargehand-out-run-1:/out", "-v", $"{nuget}:/cache/nuget", "-v", $"{npm}:/cache/npm"],
            args.Where((a, i) => a == "-v" || (i > 0 && args[i - 1] == "-v")));
        Assert.Equal(ContainerTemplate.RunArgs(Spec(), "/tmp/env"), ContainerTemplate.RunArgs(Spec([]), "/tmp/env"));
        Assert.Equal(2, ContainerTemplate.RunArgs(Spec(), "/tmp/env").Count(a => a == "-v"));
        Assert.DoesNotContain("--mount", args);
    }

    [Theory]
    [InlineData("chargehand-work-run-1")]                      // another volume of chargehand's
    [InlineData("/etc:/host")]                                  // a bind mount
    [InlineData("chargehand-cache-nuget-0123456789ab/../x")]    // a path
    [InlineData("chargehand-cache-ruby-0123456789ab")]          // no such ecosystem
    public void A_cache_that_is_not_a_cache_volume_is_refused(string volume) =>
        Assert.Throws<ArgumentException>(() => ContainerTemplate.RunArgs(Spec([volume]), "/tmp/env"));

    [Fact]
    public void One_cache_per_ecosystem()
    {
        var a = CacheVolumes.Name("npm", "o/r");
        var b = CacheVolumes.Name("npm", "o/r2");
        Assert.Throws<ArgumentException>(() => ContainerTemplate.RunArgs(Spec([a, b]), "/tmp/env"));
    }

    [Fact]
    public void The_cache_variables_may_have_only_the_values_that_point_at_the_mounts()
    {
        var env = CacheVolumes.Environment([CacheVolumes.Name("nuget", "o/r"), CacheVolumes.Name("maven", "o/r"), CacheVolumes.Name("gradle", "o/r")]);
        Assert.Equal("/cache/nuget", env["NUGET_PACKAGES"]);
        Assert.Equal("-Dmaven.repo.local=/cache/maven", env["MAVEN_OPTS"]);
        Assert.Equal("/cache/gradle", env["GRADLE_USER_HOME"]);
        Assert.Equal(["GRADLE_USER_HOME=/cache/gradle", "MAVEN_OPTS=-Dmaven.repo.local=/cache/maven", "NUGET_PACKAGES=/cache/nuget"], ContainerTemplate.EnvFileLines(env));
        Assert.Throws<ArgumentException>(() => ContainerTemplate.EnvFileLines(new Dictionary<string, string> { ["MAVEN_OPTS"] = "-javaagent:/work/x.jar" }));
        Assert.Throws<ArgumentException>(() => ContainerTemplate.EnvFileLines(new Dictionary<string, string> { ["NUGET_PACKAGES"] = "/work/.nuget" }));
        Assert.Throws<ArgumentException>(() => ContainerTemplate.EnvFileLines(new Dictionary<string, string> { ["CHARGEHAND_SERVICES"] = "postgres=evil.example:5432;x" }));
        Assert.Equal(["CHARGEHAND_SERVICES=postgres=postgres:5432,redis=redis:6379"],
            ContainerTemplate.EnvFileLines(new Dictionary<string, string> { ["CHARGEHAND_SERVICES"] = "postgres=postgres:5432,redis=redis:6379" }));
    }

    // --- services

    private static ServiceSpec Postgres(string batch = "b1", IReadOnlyDictionary<string, string>? env = null) =>
        new(batch, "postgres", PostgresImage, $"chargehand-net-{batch}", 5432, env ?? new Dictionary<string, string> { ["POSTGRES_PASSWORD"] = "test" }, ["/var/lib/postgresql/data"], 512);

    [Fact]
    public void A_service_is_a_pinned_image_on_the_batch_network_alone_with_a_few_capabilities_and_no_mount()
    {
        var args = ContainerTemplate.ServiceArgs(Postgres(), "/tmp/env");
        string After(string flag) => args[args.ToList().IndexOf(flag) + 1];
        Assert.Equal("chargehand-svc-postgres-b1", After("--name"));
        Assert.Equal("chargehand.run=b1", After("--label"));
        Assert.Equal("chargehand-net-b1", After("--network"));
        Assert.Equal("postgres", After("--network-alias"));
        Assert.Equal("ALL", After("--cap-drop"));
        Assert.Equal(["CHOWN", "DAC_OVERRIDE", "FOWNER", "SETGID", "SETUID"], args.Where((a, i) => i > 0 && args[i - 1] == "--cap-add"));
        Assert.Equal("no-new-privileges", After("--security-opt"));
        Assert.Equal("512m", After("--memory"));
        Assert.Equal("/tmp/env", After("--env-file"));
        Assert.Contains("/var/lib/postgresql/data:rw,nosuid,size=1g", args);
        Assert.Equal(PostgresImage, args[^1]);
        foreach (var flag in new[] { "-v", "--mount", "--privileged", "-p", "-P", "--publish", "--volumes-from", "--device", "--pid", "--user", "-e", "--env" })
            Assert.DoesNotContain(flag, args);
        Assert.Equal(1, args.Count(a => a == "--network"));
        Assert.DoesNotContain("TEST", string.Join(' ', args)); // the password travels in the env file only
    }

    [Fact]
    public void A_service_without_an_environment_has_no_env_file()
    {
        var args = ContainerTemplate.ServiceArgs(Postgres(env: new Dictionary<string, string>()), null);
        Assert.DoesNotContain("--env-file", args);
    }

    [Theory]
    [InlineData("name", "Postgres")]
    [InlineData("name", "post-gres")]
    [InlineData("image", "postgres:16")]
    [InlineData("tmpfs", "/var/../etc")]
    [InlineData("tmpfs", "relative")]
    [InlineData("env", "LD_PRELOAD")]
    [InlineData("env", "lower")]
    public void A_service_with_a_name_image_directory_or_variable_that_could_do_harm_is_refused(string what, string value)
    {
        var spec = what switch
        {
            "name" => Postgres() with { Name = value },
            "image" => Postgres() with { Image = value },
            "tmpfs" => Postgres() with { Tmpfs = [value] },
            _ => Postgres(env: new Dictionary<string, string> { [value] = "x" }),
        };
        Assert.Throws<ArgumentException>(() => ContainerTemplate.ServiceArgs(spec, "/tmp/env"));
    }

    [Fact]
    public async Task Services_start_in_order_and_one_that_fails_takes_the_started_ones_with_it()
    {
        var engine = new DrivenRunTests.FakeEngine();
        var redis = Postgres() with { Name = "redis", Port = 6379 };
        var ids = await new BatchServices(engine).StartAsync([Postgres(), redis], default);
        Assert.Equal(["cid-svc-postgres", "cid-svc-redis"], ids);
        Assert.Equal(["rm chargehand-svc-postgres-b1", "service postgres", "rm chargehand-svc-redis-b1", "service redis"], engine.Calls);
        Assert.Equal("postgres=postgres:5432,redis=redis:6379", BatchServices.Addresses([Postgres(), redis]));

        var failing = new DrivenRunTests.FakeEngine { OnService = s => s.Name == "redis" ? new ChargehandException(ErrorCode.ContainerUnavailable, "no image") : null };
        await Assert.ThrowsAsync<ChargehandException>(() => new BatchServices(failing).StartAsync([Postgres(), redis], default));
        Assert.Equal(["rm chargehand-svc-postgres-b1", "service postgres", "rm chargehand-svc-redis-b1", "rm chargehand-svc-postgres-b1", "rm chargehand-svc-redis-b1"], failing.Calls);
    }

    [Fact]
    public async Task After_a_restart_a_service_that_still_runs_is_kept_and_one_that_is_gone_starts_again()
    {
        var running = new DrivenRunTests.FakeEngine { HoldRunning = true };
        await new BatchServices(running).EnsureAsync([Postgres()], default);
        Assert.Empty(running.Services);

        var gone = new DrivenRunTests.FakeEngine();
        await new BatchServices(gone).EnsureAsync([Postgres()], default);
        Assert.Single(gone.Services);
    }

    [Fact]
    public async Task An_engine_that_cannot_start_services_refuses_the_batch_with_what_to_do()
    {
        var e = await Assert.ThrowsAsync<ChargehandException>(() => new BatchServices(new NoServiceEngine()).StartAsync([Postgres()], default));
        Assert.Equal(ErrorCode.ContainerUnavailable, e.Code);
        Assert.NotNull(e.Action);
        Assert.Empty(await new BatchServices(new NoServiceEngine()).StartAsync([], default)); // nothing declared, nothing needed
    }

    private sealed class NoServiceEngine : IContainerEngine
    {
        public Task<string> StartAsync(ContainerSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public Task SignalAsync(string id, string signal, CancellationToken ct) => throw new NotSupportedException();
        public Task<ContainerState> InspectAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task KillAllAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string> LogsTailAsync(string id, int bytes, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateVolumeAsync(string name, string runId, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveVolumeAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateNetworkAsync(string name, string batchId, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveNetworkAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> StartEgressAsync(EgressSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public Task ConnectNetworkAsync(string container, string network, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> OwnsAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<EngineResources> ListAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    // --- image by ecosystem

    [Fact]
    public void The_first_ecosystem_the_detector_found_that_has_an_image_picks_it_and_the_default_is_the_fallback()
    {
        var services = new DrivenSharedServices(Images: new Dictionary<string, string> { ["gradle"] = JdkImage, ["maven"] = JdkImage });
        Assert.Equal(JdkImage, SharedServicesPlan.PickImage(services, ["node", "gradle"], Image));
        Assert.Equal(Image, SharedServicesPlan.PickImage(services, ["dotnet", "node"], Image));
        Assert.Equal(Image, SharedServicesPlan.PickImage(services, [], Image));
        Assert.Equal(Image, SharedServicesPlan.PickImage(null, ["gradle"], Image));
    }

    // --- a batch

    private sealed class BatchRig : IDisposable
    {
        public TempDir Dir { get; } = new();
        public DrivenRunTests.FakeEngine Engine { get; } = new();
        public DrivenRunTests.FakeHandover Handover { get; } = new();
        public DrivenRunTests.FakeVolumes Volumes { get; } = new(_ => SessionStatus.Completed);
        public List<ContainerVerifierSettings> Verifiers { get; } = [];
        public DrivenRun Run { get; }

        public BatchRig(DrivenSharedServices? shared, string? checkoutFile = null, string remote = "https://github.com/o/r.git")
        {
            var checkout = Path.Combine(Dir.Path, "checkout");
            Directory.CreateDirectory(checkout);
            if (checkoutFile is not null)
                File.WriteAllText(Path.Combine(checkout, checkoutFile), "");
            var profile = Runs.Profile(Dir.Path) with
            {
                ClaudeCode = new ClaudeCodeSettings("2.1.283", OauthTokenSecret: "claude-code-oauth-token"),
                Driven = new DrivenSettings(true, MaxParallel: 2, Images: [Image, JdkImage], PushSecret: "secret-push", Services: shared),
            };
            var services = new DrivenServices(Engine, Engine, Volumes, _ => throw new NotSupportedException("the handover is faked"), null,
                (_, _, _) => Task.FromResult(new DrivenCheckout(checkout, Commit, remote, "main")), new DrivenRunTests.NoEvidenceProblems(), name => $"value-of-{name}",
                HandoverFor: (verifier, _) => { Verifiers.Add(verifier); return Handover; }, Poll: TimeSpan.FromMilliseconds(1));
            Run = new DrivenRun(profile, new JsonlRunLog(Path.Combine(Dir.Path, "log.jsonl")), Repo.Root, services);
        }

        public Task<ResultContract> RunAsync(string batch = "run-b1", IReadOnlyList<string>? verify = null) =>
            Run.RunAsync(new RunRequest("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit), Verify: verify),
                Driven: new RequestDriven([new DrivenTask("t1", Goal: "Add a retry")], MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m)), batch, g => $"tok-{g.RunId}", default);

        public void Dispose()
        {
            Run.Dispose();
            Dir.Dispose();
        }
    }

    private static DrivenSharedServices Shared() => new(
        ["nuget", "npm"],
        new Dictionary<string, string> { ["gradle"] = JdkImage },
        new Dictionary<string, IReadOnlyList<DrivenTestService>>
        {
            ["O/R"] = [new DrivenTestService("postgres", PostgresImage, 5432, new Dictionary<string, string> { ["POSTGRES_PASSWORD"] = "test" }, ["/var/lib/postgresql/data"], "user postgres, password test")],
        });

    [Fact]
    public async Task A_repositorys_sessions_and_verifier_share_its_caches_and_reach_its_services_and_the_services_go_before_the_network()
    {
        using var rig = new BatchRig(Shared(), "App.csproj");
        var result = await rig.RunAsync();

        Assert.Equal(ResultStatus.Completed, result.Status);
        var nuget = CacheVolumes.Name("nuget", "o/r");
        var npm = CacheVolumes.Name("npm", "o/r");

        var service = Assert.Single(rig.Engine.Services);
        Assert.Equal(("postgres", "chargehand-net-run-b1", "run-b1", 5432), (service.Name, service.Network, service.BatchId, service.Port));
        Assert.Equal("test", service.Env["POSTGRES_PASSWORD"]);
        var calls = rig.Engine.Calls;
        Assert.True(calls.IndexOf("network chargehand-net-run-b1") < calls.IndexOf("service postgres"), "the services join a network that exists");
        Assert.True(calls.IndexOf("service postgres") < calls.IndexOf("start " + rig.Engine.Started[0].RunId), "and are up before the first session");
        Assert.True(calls.IndexOf("rm chargehand-svc-postgres-run-b1", calls.IndexOf("service postgres")) < calls.IndexOf("network-rm chargehand-net-run-b1"), "and are removed before the network is");

        var session = Assert.Single(rig.Engine.Started);
        Assert.Equal([nuget, npm], session.Caches);
        Assert.Equal("/cache/nuget", session.Env["NUGET_PACKAGES"]);
        Assert.Equal("/cache/npm", session.Env["npm_config_cache"]);
        Assert.Equal("postgres=postgres:5432", session.Env["CHARGEHAND_SERVICES"]);
        Assert.Contains("postgres", session.Env["NO_PROXY"].Split(','));
        Assert.Equal(session.Env["NO_PROXY"], session.Env["no_proxy"]);
        ContainerTemplate.RunArgs(session, "/dev/null"); // every value passes the template's checks

        var verifier = Assert.Single(rig.Verifiers).Shared!;
        Assert.Equal([nuget, npm], verifier.Caches);
        Assert.Equal("postgres=postgres:5432", verifier.Environment()[ContainerTemplate.ServicesVariable]);
    }

    [Fact]
    public async Task The_session_is_told_the_repositorys_ecosystems_the_command_that_will_judge_it_and_where_the_caches_and_services_are()
    {
        using var rig = new BatchRig(Shared(), "App.csproj");
        await rig.RunAsync();

        var environment = Assert.Single(rig.Volumes.Tasks).Environment!;
        Assert.Equal(["dotnet"], environment.Ecosystems);
        Assert.Equal(["dotnet", "test"], environment.Verify);
        Assert.Equal("detected", environment.VerifySource);
        Assert.Equal(["nuget", "npm"], environment.Caches);
        Assert.Equal([new EnvironmentService("postgres", "postgres:5432", "user postgres, password test")], environment.Services);
    }

    [Fact]
    public async Task A_command_the_request_names_is_the_one_the_note_gives()
    {
        using var rig = new BatchRig(null, "App.csproj");
        await rig.RunAsync(verify: ["sh", "scripts/check.sh"]);

        var environment = Assert.Single(rig.Volumes.Tasks).Environment!;
        Assert.Equal(["sh", "scripts/check.sh"], environment.Verify);
        Assert.Equal("request", environment.VerifySource);
    }

    [Fact]
    public async Task With_no_driven_services_key_nothing_is_mounted_started_or_chosen_but_the_note_is_still_written()
    {
        using var rig = new BatchRig(null, "build.gradle");
        var result = await rig.RunAsync();

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Empty(rig.Engine.Services);
        var session = Assert.Single(rig.Engine.Started);
        Assert.Null(session.Caches);
        Assert.Equal(Image, session.ImageDigest);
        Assert.DoesNotContain("CHARGEHAND_SERVICES", session.Env.Keys);
        Assert.DoesNotContain("NUGET_PACKAGES", session.Env.Keys);
        Assert.Equal(Image, rig.Verifiers.Single().Image);
        Assert.Equal(["gradle"], Assert.Single(rig.Volumes.Tasks).Environment!.Ecosystems);
    }

    [Fact]
    public async Task A_gradle_repository_gets_the_jdk_image_for_its_session_and_its_verification_and_another_repository_keeps_the_default()
    {
        using var gradle = new BatchRig(Shared(), "build.gradle.kts");
        await gradle.RunAsync();
        Assert.Equal(JdkImage, Assert.Single(gradle.Engine.Started).ImageDigest);
        Assert.Equal(JdkImage, gradle.Verifiers.Single().Image);

        using var dotnet = new BatchRig(Shared(), "App.csproj");
        await dotnet.RunAsync();
        Assert.Equal(Image, Assert.Single(dotnet.Engine.Started).ImageDigest);
        Assert.Equal(Image, dotnet.Verifiers.Single().Image);
    }

    [Fact]
    public async Task A_repository_without_declared_services_starts_none_and_the_caches_are_the_same_whichever_way_its_remote_is_spelled()
    {
        using var other = new BatchRig(Shared(), "App.csproj", "git@github.com:o/other.git");
        await other.RunAsync();
        Assert.Empty(other.Engine.Services);
        Assert.DoesNotContain("CHARGEHAND_SERVICES", other.Engine.Started.Single().Env.Keys);

        using var ssh = new BatchRig(Shared(), "App.csproj", "git@github.com:O/R.git");
        using var https = new BatchRig(Shared(), "App.csproj", "https://github.com/o/r");
        await ssh.RunAsync();
        await https.RunAsync();
        Assert.Equal(ssh.Engine.Started.Single().Caches, https.Engine.Started.Single().Caches);
        Assert.Single(ssh.Engine.Services);
    }

    [Fact]
    public async Task A_service_that_cannot_start_ends_the_batch_before_a_session_starts_and_the_network_is_still_removed()
    {
        using var rig = new BatchRig(Shared(), "App.csproj");
        rig.Engine.OnService = _ => new ChargehandException(ErrorCode.ContainerUnavailable, "the image could not be pulled");
        var result = await rig.RunAsync();

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal(ErrorCode.ContainerUnavailable, result.Error!.Code);
        Assert.Empty(rig.Engine.Started);
        Assert.Contains("network-rm chargehand-net-run-b1", rig.Engine.Calls);
    }

    [Fact]
    public async Task The_sweep_leaves_the_services_of_a_batch_that_resumes_and_removes_those_of_a_batch_that_does_not()
    {
        using var rig = new BatchRig(Shared(), "App.csproj");
        rig.Engine.Resources = new(["chargehand-svc-postgres-run-b1", "chargehand-svc-postgres-run-old", "chargehand-svc-redis-run-old"], [], []);
        var request = new RunRequest("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([new DrivenTask("t1", Goal: "g")], MaxTokensTotal: 1, MaxUsdTotal: 1m));
        var kept = new BatchState("run-b1", DateTimeOffset.UtcNow, "t", request, null, [new TaskEntry("t1", null, "g", TaskPhase.Started, "run-t1")], []);

        var removed = await rig.Run.SweepOrphansAsync([kept], default);

        Assert.Equal(["chargehand-svc-postgres-run-old", "chargehand-svc-redis-run-old"], removed);
    }

    // --- the profile

    private static Profile Load(string driven)
    {
        using var dir = new TempDir();
        return Profile.Load(dir.Write("p.json", $$$"""{"schema":"profile/v1","driven":{{{driven}}}}"""));
    }

    private const string Pinned = "registry.example/session-jdk@sha256:3333333333333333333333333333333333333333333333333333333333333333";

    [Fact]
    public void The_profile_reads_caches_images_and_services_and_leaves_them_off_when_absent()
    {
        Assert.Null(Load("""{"enabled":true}""").Driven!.Services);
        var services = Load("""
            {"images":["JDK"],"services":{"caches":["nuget","gradle"],"images":{"gradle":"JDK"},
             "repositories":{"o/r":[{"name":"postgres","image":"PG","port":5432,"env":{"POSTGRES_PASSWORD":"test"},"tmpfs":["/var/lib/postgresql/data"],"note":"user postgres","memory_mb":256}]}}}
            """.Replace("JDK", Pinned).Replace("PG", PostgresImage)).Driven!.Services!;
        Assert.Equal(["nuget", "gradle"], services.Caches);
        Assert.Equal(Pinned, services.Images!["gradle"]);
        var postgres = Assert.Single(services.ServicesOf("O/R"));
        Assert.Equal(("postgres", 5432, 256, "user postgres"), (postgres.Name, postgres.Port, postgres.MemoryMb, postgres.Note));
        Assert.Empty(services.ServicesOf("o/none"));
    }

    [Theory]
    [InlineData("""{"services":{"caches":["ruby"]}}""", "caches entry 'ruby'")]
    [InlineData("""{"services":{"caches":["npm","npm"]}}""", "twice")]
    [InlineData("""{"services":{"images":{"gradle":"registry.example/jdk:latest"}}}""", "pinned by digest")]
    [InlineData("""{"images":[],"services":{"images":{"gradle":"registry.example/session-jdk@sha256:3333333333333333333333333333333333333333333333333333333333333333"}}}""", "must also be listed in driven.images")]
    [InlineData("""{"services":{"repositories":{"not a repo":[]}}}""", "must be owner/repo")]
    [InlineData("""{"services":{"repositories":{"o/r":[{"name":"Postgres","image":"docker.io/library/postgres@sha256:4444444444444444444444444444444444444444444444444444444444444444","port":5432}]}}}""", "name must be lowercase")]
    [InlineData("""{"services":{"repositories":{"o/r":[{"name":"db","image":"postgres:16","port":5432}]}}}""", "image must be pinned by digest")]
    [InlineData("""{"services":{"repositories":{"o/r":[{"name":"db","image":"docker.io/library/postgres@sha256:4444444444444444444444444444444444444444444444444444444444444444","port":70000}]}}}""", "port must be")]
    [InlineData("""{"services":{"repositories":{"o/r":[{"name":"db","image":"docker.io/library/postgres@sha256:4444444444444444444444444444444444444444444444444444444444444444","port":1,"env":{"LD_PRELOAD":"x"}}]}}}""", "env name 'LD_PRELOAD'")]
    [InlineData("""{"services":{"repositories":{"o/r":[{"name":"db","image":"docker.io/library/postgres@sha256:4444444444444444444444444444444444444444444444444444444444444444","port":1,"tmpfs":["../etc"]}]}}}""", "tmpfs entry")]
    public void A_services_setting_that_cannot_work_is_refused_naming_the_key(string driven, string problem)
    {
        var e = Assert.Throws<ChargehandException>(() => Load(driven));
        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Contains("driven.services", e.Message);
        Assert.Contains(problem, e.Message);
    }

    [Fact]
    public void The_profile_schema_describes_the_services_block()
    {
        using var schema = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo.Root, "profiles", "profile.schema.json")));
        var services = schema.RootElement.GetProperty("properties").GetProperty("driven").GetProperty("properties").GetProperty("services").GetProperty("properties");
        Assert.Equal(["caches", "images", "repositories"], services.EnumerateObject().Select(p => p.Name));
    }
}
