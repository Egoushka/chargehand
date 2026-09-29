using System.Net;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.OpenCode;
using Chargehand.Runtime;

namespace Chargehand.Tests;

public class OpenCodeClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, string?, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body, string? Auth)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request.Method, request.RequestUri!.PathAndQuery, body, request.Headers.Authorization?.ToString()));
            var (status, json) = respond(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    static OpenCodeClientTests() => (OpenCodeClient.ModelBootDelay, OpenCodeClient.GenerateRetryDelay) = (TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));

    private static (OpenCodeClient Client, FakeHandler Handler) Make(Func<HttpRequestMessage, string?, (HttpStatusCode, string)> respond)
    {
        var h = new FakeHandler(respond);
        return (new OpenCodeClient(new HttpClient(h) { BaseAddress = new Uri("http://127.0.0.1:4096") }, "pw"), h);
    }

    private const string Session = """{"data":{"id":"ses_1","agent":"build","model":{"id":"m","providerID":"p","variant":"default"},"location":{"directory":"/w/repo"},"outcome":null,"projectID":"x"}}""";

    [Fact]
    public async Task Create_sends_basic_auth_and_the_v2_body_shape()
    {
        var (c, h) = Make((_, _) => (HttpStatusCode.OK, Session));
        IWorkerRuntime rt = new OpenCodeWorkerRuntime(c, "2.0.16");
        var s = await rt.CreateAsync(new NodeSpec("/w/repo", "build", new ModelRef("p", "m"),
            [new PermissionRule("edit", "*", PermissionEffect.Deny)], new Dictionary<string, string> { ["run"] = "r1" }), CancellationToken.None);
        Assert.Equal("ses_1", s.Id);
        var (method, path, body, auth) = h.Seen.Single();
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/api/session", path);
        Assert.Equal("Basic " + Convert.ToBase64String("opencode:pw"u8.ToArray()), auth);
        var b = JsonDocument.Parse(body!).RootElement;
        Assert.Equal("p", b.GetProperty("model").GetProperty("providerID").GetString());
        Assert.Equal("m", b.GetProperty("model").GetProperty("id").GetString());
        Assert.False(b.GetProperty("model").TryGetProperty("variant", out _));
        Assert.Equal("/w/repo", b.GetProperty("location").GetProperty("directory").GetString());
        Assert.Equal("deny", b.GetProperty("permissions")[0].GetProperty("effect").GetString());
        Assert.Equal("r1", b.GetProperty("metadata").GetProperty("run").GetString());
    }

    /// <summary>
    /// An MCP tool's permission action is the server name, an underscore and the tool name, and a preset's "* * allow"
    /// reaches it (ADR 0034, spike O4). Servers cannot be listed instead: a checkout's own config registers its servers after the session
    /// exists, and any client can add one later. The last rule denies every action with an underscore, which covers
    /// every MCP tool, the MCP resource tools and external_directory (denied by every preset already).
    /// </summary>
    [Fact]
    public async Task Create_ends_the_rules_with_a_deny_for_every_mcp_tool()
    {
        var (c, h) = Make((_, _) => (HttpStatusCode.OK, Session));
        IWorkerRuntime rt = new OpenCodeWorkerRuntime(c, "2.0.16");
        await rt.CreateAsync(new NodeSpec("/w/repo", "build", null,
            [new PermissionRule("*", "*", PermissionEffect.Allow), new PermissionRule("edit", "*", PermissionEffect.Deny)],
            new Dictionary<string, string>()), CancellationToken.None);
        var rules = JsonDocument.Parse(h.Seen.Single().Body!).RootElement.GetProperty("permissions").EnumerateArray()
            .Select(r => $"{r.GetProperty("action").GetString()} {r.GetProperty("resource").GetString()} {r.GetProperty("effect").GetString()}");
        Assert.Equal(["* * allow", "edit * deny", "*_* * deny"], rules);
    }

    [Fact]
    public async Task Create_with_no_rules_still_denies_every_mcp_tool()
    {
        var (c, h) = Make((_, _) => (HttpStatusCode.OK, Session));
        IWorkerRuntime rt = new OpenCodeWorkerRuntime(c, "2.0.16");
        await rt.CreateAsync(new NodeSpec("/w/repo", "build", null, [], new Dictionary<string, string>()), CancellationToken.None);
        var rules = JsonDocument.Parse(h.Seen.Single().Body!).RootElement.GetProperty("permissions");
        Assert.Equal("*_*", rules.EnumerateArray().Single().GetProperty("action").GetString());
    }

    /// <summary>ADR 0026: an unset model sends no "model" in the body, so the server's base configuration default applies.</summary>
    [Fact]
    public async Task Create_with_no_model_omits_it_from_the_body()
    {
        var (c, h) = Make((_, _) => (HttpStatusCode.OK, Session));
        IWorkerRuntime rt = new OpenCodeWorkerRuntime(c, "2.0.16");
        await rt.CreateAsync(new NodeSpec("/w/repo", "build", null, [], new Dictionary<string, string>()), CancellationToken.None);
        var b = JsonDocument.Parse(h.Seen.Single().Body!).RootElement;
        Assert.False(b.TryGetProperty("model", out _));
    }

    /// <summary>ADR 0026: an unset model sends no "model" in the generate body, so the server's base default applies.</summary>
    [Fact]
    public async Task Generate_with_no_model_omits_it_from_the_body()
    {
        var (c, h) = Make((_, _) => (HttpStatusCode.OK, """{"data":{"text":"OK"}}"""));
        await c.GenerateAsync(null, null, "Say OK", CancellationToken.None);
        var b = JsonDocument.Parse(h.Seen.Single().Body!).RootElement;
        Assert.False(b.TryGetProperty("model", out _));
    }

    [Fact]
    public async Task Tagged_error_bodies_become_OpenCodeException()
    {
        var (c, _) = Make((_, _) => (HttpStatusCode.RequestEntityTooLarge,
            """{"_tag":"InstructionEntryValueTooLargeError","actualBytes":300002,"maxBytes":262144,"message":"too large"}"""));
        var e = await Assert.ThrowsAsync<OpenCodeException>(() => c.PutInstructionAsync("ses_1", "core", "x", CancellationToken.None));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, e.Status);
        Assert.Equal("InstructionEntryValueTooLargeError", e.Tag);
    }

    [Fact]
    public async Task Model_unavailable_during_location_boot_is_retried()
    {
        var calls = 0;
        var (c, _) = Make((_, _) => ++calls < 3
            ? (HttpStatusCode.BadRequest, """{"_tag":"InvalidRequestError","message":"Model unavailable: p/m"}""")
            : (HttpStatusCode.OK, """{"data":{"text":"OK"}}"""));
        Assert.Equal("OK", await c.GenerateAsync("p", "m", "Say OK", CancellationToken.None));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Other_bad_requests_are_not_retried()
    {
        var calls = 0;
        var (c, _) = Make((_, _) => { calls++; return (HttpStatusCode.BadRequest, """{"_tag":"InvalidRequestError","message":"Expected string"}"""); });
        await Assert.ThrowsAsync<OpenCodeException>(() => c.GenerateAsync("p", "m", "x", CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Always_is_never_sent()
    {
        var (c, h) = Make((_, _) => (HttpStatusCode.NoContent, ""));
        await Assert.ThrowsAsync<ArgumentException>(() => c.ReplyPermissionAsync("s", "per_1", "always", null, CancellationToken.None));
        Assert.Empty(h.Seen);
    }

    [Fact]
    public async Task Version_pin_is_enforced()
    {
        var (c, _) = Make((_, _) => (HttpStatusCode.OK, """{"version":"2.0.17","pid":1}"""));
        var e = await Assert.ThrowsAsync<ChargehandException>(() => OpenCodeWorkerRuntime.ConnectAsync(c, "2.0.16", CancellationToken.None));
        Assert.Equal(ErrorCode.RuntimeVersionMismatch, e.Code);
        Assert.NotNull(e.Action);
    }

    [Fact]
    public async Task A_server_that_is_not_running_is_runtime_unavailable_with_the_start_command()
    {
        // A port nothing listens on: bind one, then release it.
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var c = new OpenCodeClient(new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") }, "pw");

        var e = await Assert.ThrowsAsync<ChargehandException>(() => OpenCodeWorkerRuntime.ConnectAsync(c, "2.0.16", CancellationToken.None));

        Assert.Equal(ErrorCode.RuntimeUnavailable, e.Code);
        Assert.True(e.Error.Retryable);
        Assert.Equal($"Start it: scripts/opencode-serve.sh <binary> <config> {port}", e.Action);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Rate limit exceeded for api_key: k. Limit type: max_parallel_requests.", ErrorCode.RateLimited)]
    [InlineData(HttpStatusCode.TooManyRequests, "slow down", ErrorCode.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ConnectionRefused", ErrorCode.ProviderUnavailable)]
    [InlineData(HttpStatusCode.BadRequest, "Model unavailable", ErrorCode.Internal)]
    public void OpenCode_errors_map_to_codes_by_status_and_text(HttpStatusCode status, string message, ErrorCode code) =>
        Assert.Equal(code, new OpenCodeException(status, "ServiceUnavailableError", message).Code);

    [Fact]
    public void Messages_are_dispatched_on_type()
    {
        var assistant = JsonDocument.Parse("""
            {"id":"msg_a","time":{"created":1790407389533,"completed":1790407396595},"type":"assistant","agent":"build",
             "model":{"id":"gpt-x","providerID":"p","variant":"default"},
             "content":[{"type":"text","text":"Answer."},{"type":"tool","name":"read","state":{"status":"completed","input":{"path":"README.md"},"output":"see https://example.com/x"}}],
             "finish":"stop","cost":0.0002,"tokens":{"input":3,"output":195,"reasoning":201,"cache":{"read":5969,"write":424}}}
            """).RootElement;
        var m = OpenCodeWorkerRuntime.Map(assistant);
        Assert.Equal(WorkerMessageKind.Assistant, m.Kind);
        Assert.Equal("Answer.", m.Text);
        Assert.Equal("p/gpt-x", m.Model);
        Assert.Equal(new TokenCounts(3, 195, 201, 5969, 424), m.Tokens);
        Assert.Contains("https://example.com/x", m.ToolOutput, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMilliseconds(7062), m.Completed - m.Created);

        var idle = OpenCodeWorkerRuntime.Map(JsonDocument.Parse("""{"id":"msg_i","time":{"created":1},"type":"idle","outcome":"interrupted"}""").RootElement);
        Assert.Equal(IdleOutcome.Interrupted, idle.Outcome);

        var system = OpenCodeWorkerRuntime.Map(JsonDocument.Parse("""{"id":"msg_s","time":{"created":1},"type":"system","text":"Instructions updated"}""").RootElement);
        Assert.Equal(WorkerMessageKind.Other, system.Kind);
    }

    [Fact]
    public async Task Generate_retries_one_503_and_no_more()
    {
        const string Unavailable = """{"name":"ServiceUnavailableError","message":"ECONNRESET: The socket connection was closed unexpectedly."}""";
        var calls = 0;
        var (c, _) = Make((_, _) => ++calls == 1 ? (HttpStatusCode.ServiceUnavailable, Unavailable) : (HttpStatusCode.OK, """{"data":{"text":"ok"}}"""));
        Assert.Equal("ok", await c.GenerateAsync("p", "m", "hi", CancellationToken.None));
        Assert.Equal(2, calls);

        var (failing, h) = Make((_, _) => (HttpStatusCode.ServiceUnavailable, Unavailable));
        var e = await Assert.ThrowsAsync<OpenCodeException>(() => failing.GenerateAsync("p", "m", "hi", CancellationToken.None));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, e.Status);
        Assert.Equal(2, h.Seen.Count);
    }

    [Fact]
    public async Task An_mcp_server_is_added_at_a_location_with_its_config_in_the_body()
    {
        var (c, h) = Make((_, _) => (HttpStatusCode.NoContent, ""));

        await c.PutMcpServerAsync("team-docs", "/w/re po", new McpConfigBody("remote", Url: "https://mcp.example.internal/docs",
            Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer s3cret" }), CancellationToken.None);

        var (method, path, body, _) = h.Seen.Single();
        Assert.Equal(HttpMethod.Put, method);
        Assert.Equal("/api/experimental/mcp/team-docs?location%5Bdirectory%5D=%2Fw%2Fre%20po", path);
        var config = JsonDocument.Parse(body!).RootElement.GetProperty("config");
        Assert.Equal(("remote", "https://mcp.example.internal/docs", "Bearer s3cret"),
            (config.GetProperty("type").GetString(), config.GetProperty("url").GetString(), config.GetProperty("headers").GetProperty("Authorization").GetString()));
        Assert.False(config.TryGetProperty("command", out _));
    }

    [Fact]
    public void A_config_body_never_prints_a_header_or_an_environment_value()
    {
        var remote = new McpConfigBody("remote", Url: "https://mcp.example.internal/docs", Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer s3cret" });
        var local = new McpConfigBody("local", Command: ["npx", "x"], Environment: new Dictionary<string, string> { ["TOKEN"] = "s3cret" });

        Assert.DoesNotContain("s3cret", remote.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", local.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_mcp_server_is_removed_from_a_location_and_a_missing_one_is_a_tagged_404()
    {
        var (c, h) = Make((_, _) => (HttpStatusCode.NotFound, """{"_tag":"McpServerNotFoundError","server":"team-docs","message":"no such server"}"""));

        var e = await Assert.ThrowsAsync<OpenCodeException>(() => c.RemoveMcpServerAsync("team-docs", "/w/repo", CancellationToken.None));

        Assert.Equal((HttpStatusCode.NotFound, "McpServerNotFoundError"), (e.Status, e.Tag));
        var (method, path, _, _) = h.Seen.Single();
        Assert.Equal((HttpMethod.Delete, "/api/experimental/mcp/team-docs?location%5Bdirectory%5D=%2Fw%2Frepo"), (method, path));
    }

    [Fact]
    public async Task The_mcp_servers_of_a_location_are_listed_with_their_status_and_error()
    {
        var (c, h) = Make((_, _) => (HttpStatusCode.OK, """
            {"data":[{"name":"a","status":{"status":"connected"}},{"name":"b","status":{"status":"failed","error":"connection refused"}},{"name":"c","status":{"status":"pending"}}],
             "location":{"directory":"/w/repo"}}
            """));

        var servers = await c.McpServersAsync("/w/repo", CancellationToken.None);

        Assert.Equal([new McpServerStatus("a", "connected"), new McpServerStatus("b", "failed", "connection refused"), new McpServerStatus("c", "pending")], servers);
        Assert.Equal((HttpMethod.Get, "/api/mcp?location%5Bdirectory%5D=%2Fw%2Frepo"), (h.Seen.Single().Method, h.Seen.Single().Path));
    }
}
