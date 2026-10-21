using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;
using Chargehand.Runtime;
using Chargehand.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Chargehand.Tests;

/// <summary>chargehand serve on a free loopback port, over a <see cref="ScriptedRuntime"/>.</summary>
internal sealed class TestServer : IAsyncDisposable
{
    public const string Key = "test-key";

    private readonly TempDir _root;
    private readonly WebApplication _app;

    private TestServer(TempDir root, ScriptedRuntime runtime, JsonlRunLog log, WebApplication app, Uri address)
    {
        (_root, Runtime, Log, _app, BaseAddress) = (root, runtime, log, app, address);
        Http = new HttpClient { BaseAddress = address };
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
    }

    public ScriptedRuntime Runtime { get; }

    public JsonlRunLog Log { get; }

    public Uri BaseAddress { get; }

    public HttpClient Http { get; }

    public string WorkerRoot => _root.Path;

    public RunTokenService Tokens => _app.Services.GetRequiredService<RunService>().Tokens;

    public RunService Service => _app.Services.GetRequiredService<RunService>();

    /// <summary>The accounts' keys, in the file the server reads.</summary>
    public AccountKeyStore Keys => new(System.IO.Path.Combine(_root.Path, "accounts.json"));

    public static async Task<TestServer> StartAsync(ScriptedRuntime runtime, IReadOnlyList<string>? allowedHosts = null, Func<string, JsonlRunLog, Chargehand.Driven.DrivenRun>? driven = null,
        IReadOnlyList<AccountSettings>? accounts = null, TimeProvider? clock = null, bool ui = false, Func<string?, IWorkerRuntime>? runtimeFor = null, Func<string, ServerSettings, ServerSettings>? tweak = null,
        Chargehand.Automation.GitHubIntake? intake = null, Chargehand.Automation.PlaneIntake? plane = null)
    {
        var root = new TempDir();
        var log = new JsonlRunLog(System.IO.Path.Combine(root.Path, "log.jsonl"));
        var settings = new ServerSettings(0, Key, Repo.Path("presets"), AllowedHosts: allowedHosts, Accounts: accounts,
            AccountKeysFile: System.IO.Path.Combine(root.Path, "accounts.json"), Clock: clock, Ui: ui);
        var app = ChargehandServer.Create(tweak is null ? settings : tweak(root.Path, settings), Runs.Orchestrator(runtime, root.Path, log, runtimeFor), log, driven?.Invoke(root.Path, log), intake, plane: plane);
        await app.StartAsync();
        return new TestServer(root, runtime, log, app, new Uri(app.Urls.First()));
    }

    /// <summary>A client that sends <paramref name="key"/> as its bearer.</summary>
    public HttpClient As(string key)
    {
        var client = new HttpClient { BaseAddress = BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    public Task<HttpResponseMessage> PostAsync(RunRequest request, int waitSeconds) => PostAsync(JsonContent.Create(request, options: ContractJson.Options), waitSeconds);

    public Task<HttpResponseMessage> PostAsync(HttpContent content, int waitSeconds = 0)
    {
        var post = new HttpRequestMessage(HttpMethod.Post, "/v1/runs") { Content = content };
        post.Headers.Add("Prefer", $"wait={waitSeconds}");
        return Http.SendAsync(post);
    }

    public async ValueTask DisposeAsync()
    {
        Runtime.Hold.TrySetResult();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Http.Dispose();
        _root.Dispose();
    }
}

public class ServerTests
{
    private static async Task<JsonElement> Body(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Every_route_needs_the_key()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        using var anonymous = new HttpClient { BaseAddress = s.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/runs/x")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/v1/mcp", new StringContent("{}"))).StatusCode);
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/runs/x")).StatusCode);
    }

    [Fact]
    public async Task A_foreign_host_is_refused()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        var get = new HttpRequestMessage(HttpMethod.Get, "/v1/runs/x");
        get.Headers.Host = "attacker.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Http.SendAsync(get)).StatusCode);
    }

    [Fact]
    public async Task An_allowed_host_is_served_and_others_are_still_refused()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), ["chargehand.internal"]);
        HttpRequestMessage Get(string host) => new(HttpMethod.Get, "/v1/runs/x") { Headers = { Host = host } };
        Assert.Equal(HttpStatusCode.NotFound, (await s.Http.SendAsync(Get("chargehand.internal"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Http.SendAsync(Get("attacker.example"))).StatusCode);
    }

    [Fact]
    public void Listening_beyond_loopback_needs_allowed_hosts()
    {
        using var root = new TempDir();
        var log = new JsonlRunLog(System.IO.Path.Combine(root.Path, "log.jsonl"));
        var e = Assert.Throws<InvalidOperationException>(() => ChargehandServer.Create(
            new ServerSettings(0, TestServer.Key, Repo.Path("presets"), Listen: "0.0.0.0"), Runs.Orchestrator(new ScriptedRuntime(Runs.DraftReply), root.Path, log), log));
        Assert.Contains("allowed_hosts", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"contract_version":"request/v1","text":"x"}""", "context")]
    [InlineData("""{"contract_version":"request/v1","text":"x","context":{"interactive":false,"preset":"nope"}}""", "unknown preset")]
    [InlineData("""{"contract_version":"request/v1","text":"x","context":{"interactive":false,"preset":"draft"},"caller_blocks":[{"name":"g","version":"1.0.0","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","text":"t"}]}""", "sha256")]
    [InlineData("not json", "not JSON")]
    public async Task An_invalid_request_gets_its_errors(string body, string error)
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        var res = await s.PostAsync(new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains((await Body(res)).GetProperty("errors").EnumerateArray(), e => e.GetString()!.Contains(error, StringComparison.Ordinal));
        Assert.Empty(s.Runtime.IntakePrompts);
    }

    [Fact]
    public async Task A_run_that_finishes_within_the_wait_answers_with_its_result()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        var res = await s.PostAsync(Runs.DraftRequest(), waitSeconds: 30);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await Body(res);
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.Result, body));
        var id = body.GetProperty("task_id").GetString()!;
        Assert.Equal($"/v1/runs/{id}", res.Headers.Location!.OriginalString);

        // Later reads come from the run log.
        await WaitUntil(async () => (await s.Log.ReadAsync(id, CancellationToken.None)).Run is not null);
        var again = await s.Http.GetAsync($"/v1/runs/{id}");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("completed", (await Body(again)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_longer_run_answers_with_its_status_until_it_finishes()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Hold = new() };
        await using var s = await TestServer.StartAsync(runtime);
        var res = await s.PostAsync(Runs.DraftRequest(), waitSeconds: 0);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var status = await Body(res);
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.RunStatus, status));
        var location = res.Headers.Location!;

        Assert.Equal(HttpStatusCode.Accepted, (await s.Http.GetAsync(location)).StatusCode);
        runtime.Hold.SetResult();
        HttpResponseMessage done = null!;
        await WaitUntil(async () => (done = await s.Http.GetAsync(location)).StatusCode == HttpStatusCode.OK);
        Assert.Equal("completed", (await Body(done)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Events_stream_every_step_of_a_run()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Hold = new() };
        await using var s = await TestServer.StartAsync(runtime);
        var location = (await s.PostAsync(Runs.DraftRequest(), waitSeconds: 0)).Headers.Location!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var stream = await s.Http.GetAsync($"{location}/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal("text/event-stream", stream.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(timeout.Token));

        var events = new List<string>();
        while (await reader.ReadLineAsync(timeout.Token) is { } line && !events.Contains("run_finished"))
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                events.Add(line["event: ".Length..]);
                if (events[^1] == "node_started")
                    runtime.Hold.SetResult();
            }

        Assert.Equal(["accepted", "started", "intake", "node_started", "node_finished", "run_finished"], events);
    }

    [Fact]
    public async Task A_run_whose_process_ended_reads_as_lost_and_an_unknown_one_as_missing()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        await s.Log.AppendAsync(new StartRecord("run-dead", DateTimeOffset.UtcNow, new string('0', 32), Runs.DraftRequest(), int.MaxValue, BootId: "earlier-boot"), CancellationToken.None);

        var lost = await s.Http.GetAsync("/v1/runs/run-dead");
        Assert.Equal(HttpStatusCode.Gone, lost.StatusCode);
        Assert.Equal("lost", (await Body(lost)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await s.Http.GetAsync("/v1/runs/run-none")).StatusCode);
    }

    [Fact]
    public async Task A_run_from_an_earlier_boot_with_this_pid_is_lost_in_the_list_and_the_get_alike_and_one_from_this_boot_is_running()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        await s.Log.AppendAsync(new StartRecord("run-old", DateTimeOffset.UtcNow, new string('0', 32), Runs.DraftRequest(), Environment.ProcessId, BootId: "earlier-boot"), CancellationToken.None);
        await s.Log.AppendAsync(new StartRecord("run-now", DateTimeOffset.UtcNow, new string('0', 32), Runs.DraftRequest(), Environment.ProcessId, BootId: ProcessBoot.Id), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Gone, (await s.Http.GetAsync("/v1/runs/run-old")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await s.Http.GetAsync("/v1/runs/run-now")).StatusCode);
        var list = await Body(await s.Http.GetAsync("/v1/runs"));
        var states = list.GetProperty("runs").EnumerateArray().ToDictionary(r => r.GetProperty("run_id").GetString()!, r => r.GetProperty("status").GetString());
        Assert.Equal("lost", states["run-old"]);
        Assert.Equal("running", states["run-now"]);
    }

    [Fact]
    public async Task More_unfinished_runs_than_the_bound_are_refused()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Hold = new() };
        await using var s = await TestServer.StartAsync(runtime);
        for (var i = 0; i < RunService.MaxUnfinished; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await s.PostAsync(Runs.DraftRequest(), waitSeconds: 0)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await s.PostAsync(Runs.DraftRequest(), waitSeconds: 0)).StatusCode);
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData("wait=3", 3)]
    [InlineData("respond-async, wait=600", 60)]
    public void Prefer_wait_is_read_and_capped(string? prefer, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), ChargehandServer.Wait(prefer));

    [Theory]
    [InlineData(false, "", 50, 50)]    // over HTTP a research or review call answers before a client's 60 s timeout, with the run id if unfinished
    [InlineData(false, "", null, -1)]  // over stdio (no HTTP wait registered) it waits for its result
    [InlineData(true, "", 50, 10)]     // a batch runs for minutes or hours, and an MCP client cannot set Prefer per call: it gets the run id after the default wait
    [InlineData(true, "", null, 10)]
    [InlineData(true, "wait=3", 50, 3)]
    [InlineData(false, "wait=3", 50, 3)]
    public void An_MCP_call_over_HTTP_answers_within_the_client_timeout(bool driven, string prefer, int? httpWait, int seconds)
    {
        var request = driven ? new RunRequest("request/v1", "x", new RequestContext(false, "driven"), Driven: new RequestDriven([new DrivenTask("t1", Goal: "g")])) : Runs.DraftRequest();
        var wait = httpWait is { } w ? TimeSpan.FromSeconds(w) : (TimeSpan?)null;
        Assert.Equal(seconds < 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds), OrchestrateTool.WaitFor(request, prefer, wait));
    }

    [Fact]
    public async Task Health_is_liveness_and_deep_calls_the_intake_model()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply);
        await using var s = await TestServer.StartAsync(runtime);

        var live = await s.Http.GetAsync("/v1/health");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Empty(runtime.IntakePrompts);

        var deep = await s.Http.GetAsync("/v1/health?deep=1");
        Assert.Equal(HttpStatusCode.OK, deep.StatusCode);
        Assert.True(JsonDocument.Parse(await deep.Content.ReadAsStringAsync()).RootElement.GetProperty("probe").GetProperty("ok").GetBoolean());

        // The process up, every run failing: what the canary is for (seen when a gateway was retired under a running server).
        runtime.GenerateFailures.Enqueue(new InvalidOperationException("claude -p exited 1: API Error: 400"));
        var failing = await s.Http.GetAsync("/v1/health?deep=1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failing.StatusCode);
        var body = JsonDocument.Parse(await failing.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("failing", body.GetProperty("status").GetString());
        Assert.Contains("API Error: 400", body.GetProperty("probe").GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    internal static async Task WaitUntil(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition())
                return;
            await Task.Delay(50);
        }
        Assert.Fail("condition not met within 10 s");
    }
}
