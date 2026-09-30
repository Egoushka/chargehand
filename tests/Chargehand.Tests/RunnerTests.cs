using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Chargehand.Containers;
using Chargehand.Runner;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>ADR 0039: the runner is the only process that holds the container engine's socket, so what it will start is what these tests pin.</summary>
public class RunnerTests
{
    private const string Image = "registry.example/session@sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string EgressImage = "registry.example/chargehand@sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string Key = "runner-key-for-tests";

    private sealed class FakeEngine : IContainerEngine, IWorkspaceEngine
    {
        public List<string> Calls { get; } = [];
        public HashSet<string> Owned { get; } = ["ours1"];
        public int Count { get; set; }
        public ContainerSpec? Started { get; private set; }
        public EgressSpec? Egress { get; private set; }

        public Task<string> StartAsync(ContainerSpec spec, CancellationToken ct) { Started = spec; Calls.Add("start"); return Task.FromResult("ours1"); }
        public Task SignalAsync(string id, string signal, CancellationToken ct) { Calls.Add($"signal {id} {signal}"); return Task.CompletedTask; }
        public Task<ContainerState> InspectAsync(string id, CancellationToken ct) { Calls.Add($"inspect {id}"); return Task.FromResult(new ContainerState(ContainerStatus.Exited, 3, true)); }
        public Task RemoveAsync(string id, CancellationToken ct) { Calls.Add($"rm {id}"); return Task.CompletedTask; }
        public Task KillAllAsync(CancellationToken ct) { Calls.Add("kill-all"); return Task.CompletedTask; }
        public Task<string> LogsTailAsync(string id, int bytes, CancellationToken ct) { Calls.Add($"logs {id}"); return Task.FromResult("the logs"); }
        public Task CreateVolumeAsync(string name, string runId, CancellationToken ct) { Calls.Add($"volume {name} {runId}"); return Task.CompletedTask; }
        public Task RemoveVolumeAsync(string name, CancellationToken ct) { Calls.Add($"volume-rm {name}"); return Task.CompletedTask; }
        public Task CreateNetworkAsync(string name, string batchId, CancellationToken ct) { Calls.Add($"network {name} {batchId}"); return Task.CompletedTask; }
        public Task RemoveNetworkAsync(string name, CancellationToken ct) { Calls.Add($"network-rm {name}"); return Task.CompletedTask; }
        public Task<string> StartEgressAsync(EgressSpec spec, CancellationToken ct) { Egress = spec; Calls.Add("egress"); return Task.FromResult("ours1"); }
        public Task ConnectNetworkAsync(string container, string network, CancellationToken ct) { Calls.Add($"connect {container} {network}"); return Task.CompletedTask; }
        public WorkspaceSpec? Prepared { get; private set; }
        public Task PrepareWorkspaceAsync(WorkspaceSpec spec, CancellationToken ct) { Prepared = spec; Calls.Add("workspace"); return Task.CompletedTask; }
        public Task<bool> OwnsAsync(string id, CancellationToken ct) => Task.FromResult(Owned.Contains(id));
        public Task<int> CountAsync(CancellationToken ct) => Task.FromResult(Count);
    }

    private sealed class Runner(FakeEngine engine, Microsoft.AspNetCore.Builder.WebApplication app) : IAsyncDisposable
    {
        public FakeEngine Engine { get; } = engine;
        public Uri Address { get; } = new(app.Urls.First());
        public HttpClient Http { get; } = Authorized(new Uri(app.Urls.First()), Key);
        public async ValueTask DisposeAsync() { Http.Dispose(); await app.DisposeAsync(); }

        public static HttpClient Authorized(Uri address, string key)
        {
            var http = new HttpClient { BaseAddress = address };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return http;
        }
    }

    private static async Task<Runner> Start(int maxContainers = 8, IReadOnlyList<string>? hosts = null)
    {
        var engine = new FakeEngine();
        var app = RunnerServer.Create(new RunnerSettings(0, Key, new RunnerPolicy([Image], EgressImage, maxContainers, SourceRoots: ["/srv/checkouts"]), AllowedHosts: hosts), engine);
        await app.StartAsync();
        return new Runner(engine, app);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static string StartBody(string extra = "", string image = Image, string work = "chargehand-work-run1", int memory = 4096) =>
        $$"""{"run_id":"run1","image":"{{image}}","work_volume":"{{work}}","out_volume":"chargehand-out-run1","network":"chargehand-net-b1","env":{"CHARGEHAND_RUN_TOKEN":"t"},"memory_mb":{{memory}},"cpus":2,"pids":512,"command":["chargehand-session"]{{extra}}}""";

    [Fact]
    public async Task A_valid_start_reaches_the_engine_once()
    {
        await using var r = await Start();
        var response = await r.Http.PostAsync("/start", Json(StartBody()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ours1", await response.Content.ReadAsStringAsync());
        Assert.Equal(["start"], r.Engine.Calls);
        Assert.Equal(Image, r.Engine.Started!.ImageDigest);
    }

    [Fact]
    public async Task No_key_or_a_wrong_key_is_401()
    {
        await using var r = await Start();
        using var anonymous = new HttpClient { BaseAddress = r.Address };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/start", Json(StartBody()))).StatusCode);
        using var wrong = Runner.Authorized(r.Address, "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.PostAsync("/kill-all", Json("{}"))).StatusCode);
        Assert.Empty(r.Engine.Calls);
    }

    [Fact]
    public async Task Runner_rejects_unlisted_image_and_extra_fields()
    {
        await using var r = await Start();
        var other = "registry.example/other@sha256:3333333333333333333333333333333333333333333333333333333333333333";
        Assert.Equal(HttpStatusCode.Forbidden, (await r.Http.PostAsync("/start", Json(StartBody(image: other)))).StatusCode);
        foreach (var extra in new[] { ",\"privileged\":true", ",\"mounts\":[\"/:/host\"]", ",\"caps\":[\"SYS_ADMIN\"]", ",\"user\":\"0:0\"", ",\"network_mode\":\"host\"", ",\"volumes\":[\"/var/run/docker.sock:/s\"]" })
            Assert.Equal(HttpStatusCode.BadRequest, (await r.Http.PostAsync("/start", Json(StartBody(extra)))).StatusCode);
        Assert.Empty(r.Engine.Calls);
    }

    [Theory]
    [InlineData("work", "chargehand-net-b1", 4096)]                                // volume name not derived from the run id
    [InlineData("chargehand-work-run1", "host", 4096)]                             // network outside the batch prefix
    [InlineData("chargehand-work-run1", "chargehand-net-b1", 65536)]               // over the runner's memory ceiling
    public async Task Names_and_limits_are_the_runners_to_set(string work, string network, int memory)
    {
        await using var r = await Start();
        var body = StartBody(work: work, memory: memory).Replace("chargehand-net-b1", network);
        Assert.Equal(HttpStatusCode.Forbidden, (await r.Http.PostAsync("/start", Json(body))).StatusCode);
        Assert.Empty(r.Engine.Calls);
    }

    [Fact]
    public async Task Too_many_containers_is_429()
    {
        await using var r = await Start(maxContainers: 2);
        r.Engine.Count = 2;
        Assert.Equal(HttpStatusCode.TooManyRequests, (await r.Http.PostAsync("/start", Json(StartBody()))).StatusCode);
        Assert.Empty(r.Engine.Calls);
    }

    [Fact]
    public async Task A_container_that_is_not_ours_cannot_be_signalled_inspected_read_or_removed()
    {
        await using var r = await Start();
        Assert.Equal(HttpStatusCode.NotFound, (await r.Http.PostAsync("/signal", Json("""{"id":"theirs","signal":"SIGKILL"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await r.Http.GetAsync("/inspect/theirs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await r.Http.GetAsync("/logs/theirs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await r.Http.PostAsync("/remove/theirs", Json("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await r.Http.PostAsync("/connect", Json("""{"container":"theirs","network":"bridge"}"""))).StatusCode);
        Assert.Empty(r.Engine.Calls);
        Assert.Equal(HttpStatusCode.OK, (await r.Http.PostAsync("/signal", Json("""{"id":"ours1","signal":"SIGINT"}"""))).StatusCode);
        Assert.Equal(["signal ours1 SIGINT"], r.Engine.Calls);
    }

    [Fact]
    public async Task Volumes_networks_and_connect_are_limited_to_our_names()
    {
        await using var r = await Start();
        Assert.Equal(HttpStatusCode.Forbidden, (await r.Http.PostAsync("/volumes", Json("""{"name":"postgres-data","run_id":"run1"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await r.Http.PostAsync("/networks", Json("""{"name":"host","batch_id":"b1"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await r.Http.DeleteAsync("/volumes/postgres-data")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await r.Http.DeleteAsync("/networks/bridge")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await r.Http.PostAsync("/connect", Json("""{"container":"ours1","network":"chargehand-net-other"}"""))).StatusCode);
        Assert.Empty(r.Engine.Calls);
        Assert.Equal(HttpStatusCode.OK, (await r.Http.PostAsync("/volumes", Json("""{"name":"chargehand-work-run1","run_id":"run1"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await r.Http.PostAsync("/connect", Json("""{"container":"ours1","network":"bridge"}"""))).StatusCode);
    }

    [Fact]
    public async Task The_egress_image_is_the_runners_never_the_callers()
    {
        await using var r = await Start();
        var response = await r.Http.PostAsync("/egress", Json("""{"batch_id":"b1","network":"chargehand-net-b1","allow":["api.anthropic.com"]}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(EgressImage, r.Engine.Egress!.Image);
        Assert.Equal(HttpStatusCode.BadRequest, (await r.Http.PostAsync("/egress", Json("""{"batch_id":"b1","network":"chargehand-net-b1","allow":["api.anthropic.com"],"image":"evil"}"""))).StatusCode);
    }

    private static string WorkspaceBody(string source = "/srv/checkouts/repo-abc1234", string image = Image, string volume = "chargehand-work-run1", string branch = "chargehand/run1", string commit = "abc1234", string extra = "") =>
        $$"""{"run_id":"run1","image":"{{image}}","source_path":"{{source}}","work_volume":"{{volume}}","branch":"{{branch}}","commit":"{{commit}}"{{extra}}}""";

    [Fact]
    public async Task A_workspace_is_prepared_from_a_source_under_an_allowed_root_with_a_listed_image()
    {
        await using var r = await Start();
        var response = await r.Http.PostAsync("/workspace", Json(WorkspaceBody()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new WorkspaceSpec("run1", Image, "/srv/checkouts/repo-abc1234", "chargehand-work-run1", "chargehand/run1", "abc1234"), r.Engine.Prepared);
    }

    [Theory]
    [InlineData("/etc")]                                             // not under an allowed root
    [InlineData("/srv/checkouts/../secrets")]                        // a path that leaves it
    [InlineData("/srv/checkouts-evil/x")]                            // a sibling with the same prefix
    [InlineData("/srv/checkouts")]                                   // the root itself
    public async Task A_source_outside_the_allowed_roots_is_refused(string source)
    {
        await using var r = await Start();
        Assert.True((await r.Http.PostAsync("/workspace", Json(WorkspaceBody(source: source)))).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest);
        Assert.Empty(r.Engine.Calls);
    }

    [Fact]
    public async Task A_workspace_needs_a_listed_image_a_volume_named_for_the_run_and_takes_no_other_field()
    {
        await using var r = await Start();
        var other = "registry.example/other@sha256:3333333333333333333333333333333333333333333333333333333333333333";
        Assert.Equal(HttpStatusCode.Forbidden, (await r.Http.PostAsync("/workspace", Json(WorkspaceBody(image: other)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await r.Http.PostAsync("/workspace", Json(WorkspaceBody(volume: "postgres-data")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await r.Http.PostAsync("/workspace", Json(WorkspaceBody(extra: ",\"network\":\"host\"")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await r.Http.PostAsync("/workspace", Json(WorkspaceBody(branch: "main")))).StatusCode);
        Assert.Empty(r.Engine.Calls);
    }

    [Fact]
    public async Task With_no_source_roots_configured_no_workspace_is_prepared()
    {
        var engine = new FakeEngine();
        var app = RunnerServer.Create(new RunnerSettings(0, Key, new RunnerPolicy([Image], EgressImage)), engine);
        await app.StartAsync();
        using var http = Runner.Authorized(new Uri(app.Urls.First()), Key);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync("/workspace", Json(WorkspaceBody()))).StatusCode);
        await app.DisposeAsync();
    }

    [Fact]
    public async Task The_client_prepares_a_workspace_through_the_runner()
    {
        await using var r = await Start();
        await new RunnerClient(r.Http).PrepareWorkspaceAsync(new WorkspaceSpec("run1", Image, "/srv/checkouts/repo-abc1234", "chargehand-work-run1", "chargehand/run1", "abc1234"), default);
        Assert.Equal(["workspace"], r.Engine.Calls);
    }

    [Fact]
    public async Task Kill_all_by_label()
    {
        await using var r = await Start();
        Assert.Equal(HttpStatusCode.OK, (await r.Http.PostAsync("/kill-all", Json("{}"))).StatusCode);
        Assert.Equal(["kill-all"], r.Engine.Calls);
    }

    [Fact]
    public async Task The_client_round_trips_the_whole_engine_interface()
    {
        await using var r = await Start();
        var client = new RunnerClient(r.Http);
        await client.CreateNetworkAsync("chargehand-net-b1", "b1", default);
        await client.CreateVolumeAsync("chargehand-work-run1", "run1", default);
        await client.CreateVolumeAsync("chargehand-out-run1", "run1", default);
        var spec = new ContainerSpec("run1", Image, "chargehand-work-run1", "chargehand-out-run1", "chargehand-net-b1",
            new Dictionary<string, string> { ["CHARGEHAND_RUN_TOKEN"] = "t" }, 4096, 2, 512, ["chargehand-session"]);
        var id = await client.StartAsync(spec, default);
        Assert.Equal("ours1", id);
        Assert.Equal(new ContainerState(ContainerStatus.Exited, 3, true), await client.InspectAsync(id, default));
        Assert.Equal("the logs", await client.LogsTailAsync(id, 100, default));
        await client.SignalAsync(id, "SIGINT", default);
        await client.RemoveAsync(id, default);
        await client.RemoveVolumeAsync("chargehand-work-run1", default);
        await client.RemoveNetworkAsync("chargehand-net-b1", default);
        var egress = await client.StartEgressAsync(new EgressSpec("b1", "ignored-by-runner", "chargehand-net-b1", ["api.anthropic.com"]), default);
        await client.ConnectNetworkAsync(egress, "bridge", default);
        await client.KillAllAsync(default);
        Assert.Equal(spec.Env, r.Engine.Started!.Env);
        Assert.Equal(["network chargehand-net-b1 b1", "volume chargehand-work-run1 run1", "volume chargehand-out-run1 run1", "start", "inspect ours1", "logs ours1",
            "signal ours1 SIGINT", "rm ours1", "volume-rm chargehand-work-run1", "network-rm chargehand-net-b1", "egress", "connect ours1 bridge", "kill-all"], r.Engine.Calls);
    }

    [Fact]
    public async Task A_refusal_reaches_the_client_as_container_unavailable_with_an_action()
    {
        await using var r = await Start();
        var client = new RunnerClient(r.Http);
        var spec = new ContainerSpec("run1", "registry.example/x@sha256:3333333333333333333333333333333333333333333333333333333333333333", "chargehand-work-run1",
            "chargehand-out-run1", "chargehand-net-b1", new Dictionary<string, string>(), 4096, 2, 512, ["x"]);
        var e = await Assert.ThrowsAsync<ChargehandException>(() => client.StartAsync(spec, default));
        Assert.Equal(Chargehand.Contracts.ErrorCode.ContainerUnavailable, e.Code);
        Assert.Contains("allowlist", e.Message);
        Assert.NotNull(e.Action);
    }

    [Fact]
    public void A_runner_off_loopback_needs_allowed_hosts()
    {
        var e = Assert.Throws<InvalidOperationException>(() => RunnerServer.Create(
            new RunnerSettings(0, Key, new RunnerPolicy([Image], EgressImage), Listen: "0.0.0.0"), new FakeEngine()));
        Assert.Contains("allowed_hosts", e.Message);
    }

    [Fact]
    public async Task A_host_header_that_is_not_allowed_is_400()
    {
        await using var r = await Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/kill-all") { Content = Json("{}") };
        request.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await r.Http.SendAsync(request)).StatusCode);
        Assert.Empty(r.Engine.Calls);
    }
}
