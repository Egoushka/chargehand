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
}
