using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Chargehand.OpenCode;
using Chargehand.Runtime;

namespace Chargehand.Tests;

/// <summary>ADR 0034: OpenCode workers get the services a preset granted, by servers registered at the run's location.</summary>
public partial class OpenCodeServicesTests
{
    /// <summary>OpenCode's registry as the adapter sees it: servers per location, a status that turns from pending to connected.</summary>
    private sealed class FakeOpenCode(Func<string, int, string>? status = null) : HttpMessageHandler
    {
        private readonly Dictionary<(string Directory, string Name), int> _servers = [];
        private int _sessions;

        public List<(HttpMethod Method, string Path, string? Body)> Seen { get; } = [];

        /// <summary>Holds every DELETE until set; <see cref="DeleteArrived"/> completes when the first one is in.</summary>
        public TaskCompletionSource? DeleteGate { get; set; }

        public TaskCompletionSource DeleteArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The status every PUT answers with, instead of 204.</summary>
        public HttpStatusCode? PutStatus { get; set; }

        public bool DeleteThrows { get; set; }

        /// <summary>Every PUT waits for the caller to give up.</summary>
        public bool PutHangs { get; set; }

        /// <summary>Every request so far, in the order they arrived.</summary>
        public List<(HttpMethod Method, string Path, string? Body)> All => Snapshot();

        public List<(HttpMethod Method, string Path, string? Body)> Puts => Of(HttpMethod.Put);

        public List<(HttpMethod Method, string Path, string? Body)> Deletes => Of(HttpMethod.Delete);

        public List<(HttpMethod Method, string Path, string? Body)> Sessions => [.. Snapshot().Where(s => s.Path == "/api/session")];

        public int Registered { get { lock (_servers) return _servers.Count; } }

        private List<(HttpMethod Method, string Path, string? Body)> Snapshot() { lock (Seen) return [.. Seen]; }

        private List<(HttpMethod Method, string Path, string? Body)> Of(HttpMethod method) => [.. Snapshot().Where(s => s.Method == method)];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var directory = HttpUtility.ParseQueryString(request.RequestUri!.Query)["location[directory]"] ?? "";
            lock (Seen)
                Seen.Add((request.Method, request.RequestUri.PathAndQuery, body));
            if (request.Method == HttpMethod.Put && PutHangs)
                await Task.Delay(Timeout.Infinite, ct);
            if (request.Method == HttpMethod.Delete)
            {
                DeleteArrived.TrySetResult();
                if (DeleteGate is { } gate)
                    await gate.Task.WaitAsync(ct);
                if (DeleteThrows)
                    throw new HttpRequestException("reset");
            }
            return Respond(request.Method, request.RequestUri.AbsolutePath, directory, body);
        }

        private HttpResponseMessage Respond(HttpMethod method, string path, string directory, string? body)
        {
            const string prefix = "/api/experimental/mcp/";
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                var name = Uri.UnescapeDataString(path[prefix.Length..]);
                lock (_servers)
                {
                    if (method == HttpMethod.Put && PutStatus is { } refused)
                        return Json(refused, """{"_tag":"InvalidRequestError","message":"bad config"}""");
                    if (method == HttpMethod.Put)
                        _servers[(directory, name)] = 0;
                    else if (!_servers.Remove((directory, name)))
                        return Json(HttpStatusCode.NotFound, $$"""{"_tag":"McpServerNotFoundError","server":"{{name}}","message":"no such server"}""");
                }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (method == HttpMethod.Get && path == "/api/mcp")
            {
                lock (_servers)
                {
                    var items = _servers.Keys.Where(k => k.Directory == directory).ToList()
                        .Select(k => $$$"""{"name":"{{{k.Name}}}","status":{{{(status ?? Default)(k.Name, _servers[k] = _servers[k] + 1)}}}}""");
                    return Json(HttpStatusCode.OK, $$$"""{"data":[{{{string.Join(",", items)}}}],"location":{"directory":"{{{directory}}}"}}""");
                }
            }
            if (method == HttpMethod.Post && (path == "/api/session" || path.EndsWith("/fork", StringComparison.Ordinal)))
            {
                var dir = path == "/api/session" ? JsonDocument.Parse(body!).RootElement.GetProperty("location").GetProperty("directory").GetString() : "/w/repo";
                return Json(HttpStatusCode.OK, $$$"""{"data":{"id":"ses_{{{Interlocked.Increment(ref _sessions)}}}","agent":"build","model":{"id":"m","providerID":"p","variant":"default"},"location":{"directory":"{{{dir}}}"},"outcome":null,"projectID":"x"}}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private static string Default(string name, int polls) => polls < 2 ? """{"status":"pending"}""" : """{"status":"connected"}""";

        private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static readonly ServiceGrant Docs = new("team-docs",
        new HttpServiceTransport(new Uri("https://mcp.example.internal/docs"), new Dictionary<string, string> { ["Authorization"] = "Bearer s3cret" }),
        ["search_docs", "read_doc"], new string('a', 64));

    private static (OpenCodeWorkerRuntime Runtime, FakeOpenCode Server) Make(Func<string, int, string>? status = null, TimeSpan? connectTimeout = null, TimeSpan? settle = null)
    {
        var server = new FakeOpenCode(status);
        var client = new OpenCodeClient(new HttpClient(server) { BaseAddress = new Uri("http://127.0.0.1:4096") }, "pw");
        return (new OpenCodeWorkerRuntime(client, "2.0.18", new ServiceTimings(connectTimeout ?? TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(5), settle ?? TimeSpan.Zero)), server);
    }

    private static NodeSpec Spec(string run, params ServiceGrant[] grants) =>
        new("/w/repo", "build", new ModelRef("p", "m"), [new PermissionRule("*", "*", PermissionEffect.Allow)],
            new Dictionary<string, string> { [IRunCleanup.RunMetadataKey] = run }, grants);

    private static Task<WorkerSession> Create(OpenCodeWorkerRuntime rt, string run, params ServiceGrant[] grants) =>
        ((IWorkerRuntime)rt).CreateAsync(Spec(run, grants), CancellationToken.None);

    private static Task End(OpenCodeWorkerRuntime rt, string run) => ((IRunCleanup)rt).EndRunAsync(run, CancellationToken.None);

    private static IReadOnlyDictionary<string, string> Unavailable(OpenCodeWorkerRuntime rt, string sessionId) => ((IServiceHealth)rt).UnavailableServices(sessionId);

    private static string RegisteredName((HttpMethod Method, string Path, string? Body) request) =>
        Uri.UnescapeDataString(request.Path["/api/experimental/mcp/".Length..request.Path.IndexOf('?', StringComparison.Ordinal)]);

    private static List<string> Rules((HttpMethod Method, string Path, string? Body) session) =>
        [.. JsonDocument.Parse(session.Body!).RootElement.GetProperty("permissions").EnumerateArray()
            .Select(r => $"{r.GetProperty("action").GetString()} {r.GetProperty("resource").GetString()} {r.GetProperty("effect").GetString()}")];

    [GeneratedRegex("^team-docs-[0-9a-f]{8}$")]
    private static partial Regex RegisteredDocs();

    [Fact]
    public async Task A_remote_grant_is_registered_for_the_location_before_the_session_is_created()
    {
        var (rt, server) = Make();

        await Create(rt, "run-1", Docs);

        var put = Assert.Single(server.Puts);
        Assert.Matches(RegisteredDocs(), RegisteredName(put));
        Assert.Equal("/w/repo", HttpUtility.ParseQueryString(new Uri("http://x" + put.Path).Query)["location[directory]"]);
        using var body = JsonDocument.Parse(put.Body!);
        var config = body.RootElement.GetProperty("config");
        Assert.Equal(("remote", "https://mcp.example.internal/docs", "Bearer s3cret"),
            (config.GetProperty("type").GetString(), config.GetProperty("url").GetString(), config.GetProperty("headers").GetProperty("Authorization").GetString()));
    }

    [Fact]
    public async Task The_server_is_registered_and_seen_connected_before_the_session_is_created()
    {
        var (rt, server) = Make();

        await Create(rt, "run-1", Docs);

        var order = string.Join(" ", server.Puts.Concat(server.Sessions).Select(s => s.Method.Method));
        Assert.Equal("PUT POST", order);
        Assert.Equal("/api/session", server.Sessions.Single().Path);
    }

    [Fact]
    public async Task A_server_is_given_a_moment_after_it_reads_connected_before_the_session_is_created()
    {
        var (rt, server) = Make(settle: TimeSpan.FromMilliseconds(300));

        var started = DateTime.UtcNow;
        await Create(rt, "run-1", Docs);

        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(280));
        Assert.Single(server.Sessions);
    }

    [Fact]
    public async Task A_stdio_grant_is_registered_as_a_local_server_with_its_environment()
    {
        var notes = new ServiceGrant("team-notes", new StdioServiceTransport(["npx", "-y", "example-notes-mcp"], new Dictionary<string, string> { ["NOTES_TOKEN"] = "s3cret" }), ["search_notes"], new string('b', 64));
        var (rt, server) = Make();

        await Create(rt, "run-1", notes);

        using var body = JsonDocument.Parse(Assert.Single(server.Puts).Body!);
        var config = body.RootElement.GetProperty("config");
        Assert.Equal("local", config.GetProperty("type").GetString());
        Assert.Equal(["npx", "-y", "example-notes-mcp"], config.GetProperty("command").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal("s3cret", config.GetProperty("environment").GetProperty("NOTES_TOKEN").GetString());
        Assert.False(config.TryGetProperty("headers", out _));
    }

    [Fact]
    public async Task The_rules_end_with_the_deny_and_one_allow_per_granted_tool()
    {
        var (rt, server) = Make();

        await Create(rt, "run-1", Docs);

        var name = RegisteredName(Assert.Single(server.Puts));
        Assert.Equal(["* * allow", "*_* * deny", $"{name}_search_docs * allow", $"{name}_read_doc * allow"], Rules(Assert.Single(server.Sessions)));
    }

    [Fact]
    public async Task A_tool_name_is_written_the_way_opencode_writes_it_into_an_action()
    {
        var odd = Docs with { Tools = ["search.docs", "list*", "read_doc"] };
        var (rt, server) = Make();

        await Create(rt, "run-1", odd);

        var name = RegisteredName(Assert.Single(server.Puts));
        Assert.Equal([$"{name}_search_docs * allow", $"{name}_list_ * allow", $"{name}_read_doc * allow"], Rules(Assert.Single(server.Sessions))[2..]);
    }

    [Fact]
    public async Task A_grant_with_no_tools_registers_nothing()
    {
        var (rt, server) = Make();

        await Create(rt, "run-1", Docs with { Tools = [] });

        Assert.Empty(server.Puts);
        Assert.Equal(["* * allow", "*_* * deny"], Rules(Assert.Single(server.Sessions)));
    }

    [Fact]
    public async Task No_grants_call_no_mcp_route()
    {
        var (rt, server) = Make();

        var session = await ((IWorkerRuntime)rt).CreateAsync(new NodeSpec("/w/repo", "build", null, [], new Dictionary<string, string>()), CancellationToken.None);
        await End(rt, "run-1");

        Assert.Empty(Unavailable(rt, session.Id));
        Assert.DoesNotContain(server.Sessions, s => s.Path.Contains("/mcp", StringComparison.Ordinal));
        Assert.Empty(server.Puts);
        Assert.Empty(server.Deletes);
    }

    [Fact]
    public async Task A_grant_without_the_run_id_is_refused_before_anything_is_registered()
    {
        var (rt, server) = Make();

        await Assert.ThrowsAsync<ArgumentException>(() => ((IWorkerRuntime)rt).CreateAsync(
            new NodeSpec("/w/repo", "build", null, [], new Dictionary<string, string>(), [Docs]), CancellationToken.None));

        Assert.Empty(server.Puts);
        Assert.Empty(server.Sessions);
    }

    [Fact]
    public async Task A_server_that_fails_to_connect_is_dropped_removed_and_reported_as_unavailable()
    {
        var (rt, server) = Make((_, _) => """{"status":"failed","error":"connection refused"}""");

        var session = await Create(rt, "run-1", Docs);

        Assert.Equal("ses_1", session.Id);
        Assert.Equal(["* * allow", "*_* * deny"], Rules(Assert.Single(server.Sessions)));
        Assert.Equal("failed: connection refused", Assert.Single(Unavailable(rt, session.Id), u => u.Key == "team-docs").Value);
        Assert.Single(server.Deletes);
        Assert.Equal(0, server.Registered);
    }

    [Fact]
    public async Task A_server_that_needs_a_login_is_unavailable_too()
    {
        var (rt, _) = Make((_, _) => """{"status":"needs_auth","error":"requires a login"}""");

        var session = await Create(rt, "run-1", Docs);

        Assert.Equal("needs_auth: requires a login", Unavailable(rt, session.Id)["team-docs"]);
    }

    [Fact]
    public async Task A_server_that_stays_pending_is_unavailable_after_the_timeout()
    {
        var (rt, server) = Make((_, _) => """{"status":"pending"}""", TimeSpan.FromMilliseconds(150));

        var session = await Create(rt, "run-1", Docs);

        Assert.StartsWith("pending: no connection within", Unavailable(rt, session.Id)["team-docs"], StringComparison.Ordinal);
        Assert.Equal(0, server.Registered);
    }

    [Fact]
    public async Task What_the_server_says_about_a_failure_never_carries_a_credential()
    {
        var (rt, _) = Make((_, _) => """{"status":"failed","error":"401 for Authorization: Bearer s3cret, retry with s3cret"}""");

        var session = await Create(rt, "run-1", Docs);

        var status = Unavailable(rt, session.Id)["team-docs"];
        Assert.DoesNotContain("s3cret", status, StringComparison.Ordinal);
        Assert.StartsWith("failed: 401", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_put_the_server_refuses_is_unavailable_with_the_servers_reason()
    {
        var (rt, server) = Make();
        server.PutStatus = HttpStatusCode.BadRequest;

        var session = await Create(rt, "run-1", Docs);

        Assert.StartsWith("rejected: OpenCode 400 InvalidRequestError:", Unavailable(rt, session.Id)["team-docs"], StringComparison.Ordinal);
        Assert.Equal(["* * allow", "*_* * deny"], Rules(Assert.Single(server.Sessions)));
    }

    [Fact]
    public async Task A_put_that_never_answers_is_bounded_by_the_connect_timeout()
    {
        var (rt, server) = Make(connectTimeout: TimeSpan.FromMilliseconds(150));
        server.PutHangs = true;

        var session = await Create(rt, "run-1", Docs);

        Assert.StartsWith("rejected: no answer within", Unavailable(rt, session.Id)["team-docs"], StringComparison.Ordinal);
        Assert.Equal(["* * allow", "*_* * deny"], Rules(Assert.Single(server.Sessions)));
    }

    [Fact]
    public async Task Every_node_of_the_run_reports_the_server_the_first_one_could_not_connect_and_a_fork_inherits_it()
    {
        var (rt, server) = Make((_, _) => """{"status":"failed","error":"connection refused"}""");

        var first = await Create(rt, "run-1", Docs);
        var second = await Create(rt, "run-1", Docs);
        var fork = await ((IWorkerRuntime)rt).ForkAsync(first.Id, null, CancellationToken.None);

        Assert.All(new[] { first, second, fork }, s => Assert.Equal("failed: connection refused", Unavailable(rt, s.Id)["team-docs"]));
        Assert.Single(server.Puts);
    }

    [Fact]
    public void A_session_that_is_unknown_has_no_unavailable_service()
    {
        var (rt, _) = Make();

        Assert.Empty(Unavailable(rt, "ses_none"));
    }

    [Fact]
    public async Task A_connected_server_is_not_reported()
    {
        var (rt, _) = Make();

        var session = await Create(rt, "run-1", Docs);

        Assert.Empty(Unavailable(rt, session.Id));
    }

    [Fact]
    public async Task Nodes_of_one_run_share_one_registration_until_the_run_ends()
    {
        var (rt, server) = Make();

        await Task.WhenAll(Create(rt, "run-1", Docs), Create(rt, "run-1", Docs));
        await Create(rt, "run-1", Docs);

        Assert.Single(server.Puts);
        Assert.Empty(server.Deletes);
        await End(rt, "run-1");
        Assert.Single(server.Deletes);
    }

    [Fact]
    public async Task A_fork_made_after_its_parent_ended_still_has_the_registration()
    {
        var (rt, server) = Make();
        var parent = await Create(rt, "run-1", Docs);

        var fork = await ((IWorkerRuntime)rt).ForkAsync(parent.Id, null, CancellationToken.None);

        Assert.NotEqual(parent.Id, fork.Id);
        Assert.Empty(server.Deletes);
        await End(rt, "run-1");
        Assert.Single(server.Puts);
        Assert.Single(server.Deletes);
    }

    [Fact]
    public async Task Two_runs_at_one_location_share_a_server_and_each_session_keeps_its_own_tools()
    {
        var (rt, server) = Make();
        var readOnly = Docs with { Tools = ["read_doc"] };

        await Task.WhenAll(Create(rt, "run-1", Docs), Create(rt, "run-2", readOnly));
        var name = RegisteredName(Assert.Single(server.Puts));
        var granted = server.Sessions.Select(s => string.Join(", ", Rules(s).Where(r => r.StartsWith(name, StringComparison.Ordinal)).Order())).ToList();

        Assert.Contains($"{name}_read_doc * allow, {name}_search_docs * allow", granted);
        Assert.Contains($"{name}_read_doc * allow", granted);

        await End(rt, "run-1");
        Assert.Empty(server.Deletes);
        await End(rt, "run-2");
        Assert.Equal(name, RegisteredName(Assert.Single(server.Deletes)));
        Assert.Equal(0, server.Registered);
    }

    [Fact]
    public async Task A_run_joining_a_server_that_is_still_connecting_waits_for_it_and_registers_nothing_twice()
    {
        var (rt, server) = Make((_, polls) => polls < 6 ? """{"status":"pending"}""" : """{"status":"connected"}""");

        await Task.WhenAll(Create(rt, "run-1", Docs), Create(rt, "run-2", Docs));

        Assert.Single(server.Puts);
        Assert.Equal(2, server.Sessions.Count);
        Assert.All(server.Sessions, s => Assert.Contains("_search_docs", s.Body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Runs_with_different_credentials_get_different_servers_and_one_ends_without_touching_the_other()
    {
        var rotated = Docs with { Transport = new HttpServiceTransport(new Uri("https://mcp.example.internal/docs"), new Dictionary<string, string> { ["Authorization"] = "Bearer other" }) };
        var (rt, server) = Make();

        await Create(rt, "run-1", Docs);
        await Create(rt, "run-2", rotated);
        var names = server.Puts.Select(RegisteredName).ToList();

        Assert.Equal(2, names.Distinct().Count());
        Assert.All(names, n => Assert.Matches(RegisteredDocs(), n));
        await End(rt, "run-1");
        Assert.Equal(names[0], RegisteredName(Assert.Single(server.Deletes)));
        Assert.Equal(1, server.Registered);
    }

    [Fact]
    public async Task The_same_credentials_get_the_same_name_and_no_name_shows_them()
    {
        var (rt, server) = Make();

        await Create(rt, "run-1", Docs);
        await End(rt, "run-1");
        await Create(rt, "run-2", Docs);

        Assert.Equal(2, server.Puts.Count);
        Assert.Equal(RegisteredName(server.Puts[0]), RegisteredName(server.Puts[1]));
        Assert.DoesNotContain("s3cret", RegisteredName(server.Puts[0]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtimes_name_the_same_server_differently()
    {
        var (first, firstServer) = Make();
        var (second, secondServer) = Make();

        await Create(first, "run-1", Docs);
        await Create(second, "run-1", Docs);

        Assert.NotEqual(RegisteredName(Assert.Single(firstServer.Puts)), RegisteredName(Assert.Single(secondServer.Puts)));
    }

    [Fact]
    public async Task A_run_starting_while_the_last_holder_removes_the_server_registers_it_again_after_the_removal()
    {
        var (rt, server) = Make();
        server.DeleteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Create(rt, "run-1", Docs);

        var ending = End(rt, "run-1");
        await server.DeleteArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var starting = Create(rt, "run-2", Docs);
        await Task.Delay(100);
        Assert.Single(server.Puts);

        server.DeleteGate.SetResult();
        await Task.WhenAll(ending, starting);

        Assert.Equal(["PUT", "DELETE", "PUT"], server.All.Where(s => s.Method != HttpMethod.Get && s.Path.StartsWith("/api/experimental/mcp/", StringComparison.Ordinal)).Select(s => s.Method.Method));
        Assert.Equal(1, server.Registered);
        Assert.Contains("_search_docs", server.Sessions.Last().Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ending_a_run_twice_removes_the_server_once()
    {
        var (rt, server) = Make();
        await Create(rt, "run-1", Docs);

        await End(rt, "run-1");
        await End(rt, "run-1");

        Assert.Single(server.Deletes);
    }

    [Fact]
    public async Task A_run_cancelled_while_its_server_connects_leaves_nothing_registered_once_it_ends()
    {
        var (rt, server) = Make((_, _) => """{"status":"pending"}""", TimeSpan.FromMilliseconds(200));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ((IWorkerRuntime)rt).CreateAsync(Spec("run-1", Docs), cts.Token));
        await End(rt, "run-1");

        Assert.Single(server.Deletes);
        Assert.Equal(0, server.Registered);
    }

    [Fact]
    public async Task A_removal_that_fails_does_not_throw_out_of_the_end_of_the_run()
    {
        var (rt, server) = Make();
        server.DeleteThrows = true;
        await Create(rt, "run-1", Docs);

        await End(rt, "run-1");

        Assert.Single(server.Deletes);
    }

    [Fact]
    public async Task A_server_that_is_already_gone_is_not_a_failure()
    {
        var (rt, server) = Make();
        await Create(rt, "run-1", Docs);
        var client = new OpenCodeClient(new HttpClient(server, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1:4096") }, "pw");
        await client.RemoveMcpServerAsync(RegisteredName(server.Puts[0]), "/w/repo", CancellationToken.None);

        await End(rt, "run-1");

        Assert.Equal(2, server.Deletes.Count);
        Assert.Equal(0, server.Registered);
    }
}
