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

    private sealed class FakeEngine : IContainerEngine, IWorkspaceEngine, IOutVolumeEngine
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
        public Dictionary<string, byte[]> OutFiles { get; } = [];
        public Task WriteOutFileAsync(OutFileSpec spec, ReadOnlyMemory<byte> content, CancellationToken ct) { Calls.Add($"out-write {spec.OutVolume} {spec.Name}"); OutFiles[spec.Name] = content.ToArray(); return Task.CompletedTask; }
        public async Task<bool> ReadOutFileAsync(OutFileSpec spec, Stream destination, CancellationToken ct)
        {
            Calls.Add($"out-read {spec.OutVolume} {spec.Name}");
            if (!OutFiles.TryGetValue(spec.Name, out var bytes))
                return false;
            await destination.WriteAsync(bytes, ct);
            return true;
        }
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
        var app = RunnerServer.Create(new RunnerSettings(0, Key, new RunnerPolicy([Image], EgressImage, maxContainers, SourceRoots: ["/srv/checkouts"], OutsideNetworks: ["stack_net"], Forwards: ["4300=chargehand:4300"]), AllowedHosts: hosts), engine);
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
    public async Task A_container_may_join_a_configured_outside_network_and_no_other()
    {
        await using var r = await Start();
        Assert.Equal(HttpStatusCode.OK, (await r.Http.PostAsync("/connect", Json("""{"container":"ours1","network":"stack_net"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await r.Http.PostAsync("/connect", Json("""{"container":"ours1","network":"db_net"}"""))).StatusCode);
        Assert.Equal(["connect ours1 stack_net"], r.Engine.Calls);
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

    private static string OutUrl(string name, string run = "run1", string image = Image) => $"/out/{run}/{name}?image={Uri.EscapeDataString(image)}";

    [Fact]
    public async Task The_task_file_goes_into_the_run_s_output_volume_and_the_results_come_out_through_the_client()
    {
        await using var r = await Start();
        var client = new RunnerClient(r.Http);
        await client.WriteOutFileAsync(new OutFileSpec("run1", Image, "chargehand-out-run1", "task.json"), "{\"goal\":\"g\"}"u8.ToArray(), default);
        Assert.Equal("{\"goal\":\"g\"}", Encoding.UTF8.GetString(r.Engine.OutFiles["task.json"]));
        r.Engine.OutFiles["chargehand.bundle"] = [1, 2, 3, 0, 255];
        using var read = new MemoryStream();
        Assert.True(await client.ReadOutFileAsync(new OutFileSpec("run1", Image, "chargehand-out-run1", "chargehand.bundle"), read, default));
        Assert.Equal(new byte[] { 1, 2, 3, 0, 255 }, read.ToArray());
        Assert.False(await client.ReadOutFileAsync(new OutFileSpec("run1", Image, "chargehand-out-run1", "session-outcome.json"), new MemoryStream(), default));
        Assert.Contains("out-write chargehand-out-run1 task.json", r.Engine.Calls);
    }

    [Fact]
    public async Task Out_files_need_a_listed_image_a_fixed_name_and_the_right_direction()
    {
        await using var r = await Start();
        var other = "registry.example/other@sha256:3333333333333333333333333333333333333333333333333333333333333333";
        var content = new ByteArrayContent([1]);
        Assert.Equal(HttpStatusCode.Forbidden, (await r.Http.PutAsync(OutUrl("task.json", image: other), content)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await r.Http.PutAsync(OutUrl("chargehand.bundle"), content)).StatusCode);        // only task.json goes in
        Assert.Equal(HttpStatusCode.Forbidden, (await r.Http.GetAsync(OutUrl("task.json"))).StatusCode);                        // only the results come out
        Assert.Equal(HttpStatusCode.Forbidden, (await r.Http.GetAsync(OutUrl("stream.jsonl"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await r.Http.GetAsync(OutUrl("session-outcome.json", image: other))).StatusCode);
        Assert.True((await r.Http.GetAsync(OutUrl("session-outcome.json", run: "a;b"))).StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden);
        Assert.Empty(r.Engine.Calls);
    }

    [Fact]
    public async Task Out_files_need_the_runner_key()
    {
        await using var r = await Start();
        using var http = new HttpClient { BaseAddress = r.Address };
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(OutUrl("session-outcome.json"))).StatusCode);
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
    public async Task Removing_a_container_that_is_not_there_is_not_an_error()
    {
        // A task that fails before its container starts still cleans up by the container's fixed name; the runner answers 404 for a name it does
        // not own or that is gone, and the client must not let that replace the failure that came first (DockerCliEngine's `rm -f` is silent too).
        await using var r = await Start();
        var client = new RunnerClient(r.Http);
        await client.RemoveAsync("chargehand-run-not-started", default);
        Assert.Empty(r.Engine.Calls);
    }

    [Fact]
    public async Task An_egress_carries_the_forwards_the_runner_allows_and_refuses_any_other()
    {
        // Without this the egress container started with no forward at all: a session's call back to the server was refused (measured on the first
        // batch through a runner on a Linux host), because the runner's egress request had no field for it.
        await using var r = await Start();
        var client = new RunnerClient(r.Http);
        await client.StartEgressAsync(new EgressSpec("b1", "ignored-by-runner", "chargehand-net-b1", ["api.anthropic.com"], Forwards: ["4300=chargehand:4300"]), default);
        Assert.Equal(["4300=chargehand:4300"], r.Engine.Egress!.Forwards);
        Assert.Equal(EgressImage, r.Engine.Egress.Image);

        var refused = await Assert.ThrowsAsync<ChargehandException>(() => client.StartEgressAsync(
            new EgressSpec("b2", "ignored-by-runner", "chargehand-net-b2", ["api.anthropic.com"], Forwards: ["4300=evil.example:443"]), default));
        Assert.Equal(Chargehand.Contracts.ErrorCode.ContainerUnavailable, refused.Code);
        Assert.Contains("forward", refused.Message);
        Assert.Equal("b1", r.Engine.Egress.BatchId);   // the refused request reached no engine
    }

    [Fact]
    public async Task An_egress_without_forwards_still_starts()
    {
        await using var r = await Start();
        var client = new RunnerClient(r.Http);
        await client.StartEgressAsync(new EgressSpec("b1", "ignored-by-runner", "chargehand-net-b1", ["api.anthropic.com"]), default);
        Assert.True(r.Engine.Egress!.Forwards is null or { Count: 0 });
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
