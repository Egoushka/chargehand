using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Chargehand.Egress;

namespace Chargehand.Tests;

/// <summary>ADR 0039, decision 9: the model endpoint of the egress container. The upstream is an in-process fake handler, so no TLS and no network is involved; the gateway itself
/// listens on a loopback port and is spoken to with a plain HTTP client, as a session's Claude Code would.</summary>
public class ModelGatewayTests
{
    private const string Real = "REAL-CREDENTIAL-do-not-leak-0123456789";
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Key = ModelTokens.NewKey();

    private const string StreamBody =
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"m1\",\"usage\":{\"input_tokens\":120,\"output_tokens\":1,\"cache_read_input_tokens\":5000}}}\n\n"
        + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"hi\"}}\n\n"
        + "event: message_delta\ndata: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":30}}\n\n";

    private sealed class FakeUpstream(Func<HttpRequestMessage, HttpResponseMessage>? answer = null) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body, string? Authorization)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Calls)
                Calls.Add((request, body, request.Headers.Authorization?.ToString()));
            if (answer is not null)
                return answer(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(StreamBody, Encoding.UTF8, "text/event-stream") };
            response.Headers.TryAddWithoutValidation("request-id", "req_1");
            response.Headers.TryAddWithoutValidation("set-cookie", "a=b");
            return response;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public ModelGateway Gateway { get; }
        public FakeUpstream Upstream { get; }
        public List<GatewayEvent> Log { get; } = [];
        public DateTimeOffset Clock { get; set; } = Now;
        public HttpClient Client { get; } = new();
        public string Url { get; }

        public Rig(Func<HttpRequestMessage, HttpResponseMessage>? answer = null)
        {
            Upstream = new FakeUpstream(answer);
            Gateway = new ModelGateway(Key, Real, "api.example.test", Upstream, () => Clock, e => { lock (Log) Log.Add(e); });
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}";
            Gateway.Start(Url + "/");
        }

        public static string Token(string run = "run-a", long cap = 0, int minutes = 30) => ModelTokens.Mint(Key, run, Now.AddMinutes(minutes), cap);

        public Task<HttpResponseMessage> Post(string? token, string path = "/v1/messages?beta=true", string body = """{"model":"m","messages":[]}""", bool apiKey = false)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Url + path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (token is not null && apiKey)
                request.Headers.TryAddWithoutValidation("x-api-key", token);
            else if (token is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
            return Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Gateway.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_request_without_a_valid_token_is_refused_with_401_and_never_reaches_the_upstream()
    {
        await using var rig = new Rig();
        foreach (var token in new[] { null, "", "provider-token-lookalike", "chm-garbage.garbage", ModelTokens.Mint(ModelTokens.NewKey(), "run-a", Now.AddMinutes(5), 0) })
            Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Post(token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Post(Rig.Token(minutes: -1))).StatusCode);   // expired
        Assert.Empty(rig.Upstream.Calls);
        Assert.All(rig.Log, e => Assert.Equal(401, e.Status));
    }

    [Fact]
    public async Task A_valid_token_is_swapped_for_the_real_credential_and_the_response_streams_back()
    {
        await using var rig = new Rig();
        var token = Rig.Token();
        var response = await rig.Post(token, body: """{"model":"m","messages":[{"role":"user","content":"hello"}]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(StreamBody, await response.Content.ReadAsStringAsync());
        var (request, body, authorization) = Assert.Single(rig.Upstream.Calls);
        Assert.Equal($"Bearer {Real}", authorization);
        Assert.Equal("https://api.example.test/v1/messages?beta=true", request.RequestUri!.ToString());
        Assert.Equal("""{"model":"m","messages":[{"role":"user","content":"hello"}]}""", body);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
        Assert.False(request.Headers.Contains("x-api-key"));
        Assert.False(request.Headers.Contains("Accept-Encoding"));              // the response must be readable for its usage
        Assert.DoesNotContain(token, request.Headers.Authorization!.ToString());
        // Response headers: the API's own pass, a cookie does not, and the real credential is nowhere.
        Assert.Equal("req_1", response.Headers.GetValues("request-id").Single());
        Assert.False(response.Headers.Contains("set-cookie"));
        Assert.DoesNotContain(Real, string.Join('\n', response.Headers.Select(h => string.Join(',', h.Value))));
    }

    [Fact]
    public async Task The_token_is_also_accepted_in_x_api_key()
    {
        await using var rig = new Rig();
        Assert.Equal(HttpStatusCode.OK, (await rig.Post(Rig.Token(), apiKey: true)).StatusCode);
        Assert.Equal($"Bearer {Real}", Assert.Single(rig.Upstream.Calls).Authorization);
    }

    [Theory]
    [InlineData("/api/oauth/token")]
    [InlineData("/v1/../api/x")]
    [InlineData("/v1//evil.test/x")]
    [InlineData("/")]
    [InlineData("/v1")]
    public async Task Only_the_versioned_api_is_forwarded(string path)
    {
        await using var rig = new Rig();
        var status = (await rig.Post(Rig.Token(), path)).StatusCode;
        Assert.Contains(status, new[] { HttpStatusCode.NotFound, HttpStatusCode.BadRequest });
        Assert.Empty(rig.Upstream.Calls);
    }

    [Fact]
    public async Task A_redirect_is_returned_without_its_location_and_never_followed()
    {
        await using var rig = new Rig(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://evil.test/steal");
            return redirect;
        });
        var response = await rig.Post(Rig.Token());
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Single(rig.Upstream.Calls);
    }

    [Fact]
    public async Task Usage_counts_input_plus_output_from_the_stream_and_a_run_over_its_cap_is_refused_without_a_retry_status()
    {
        await using var rig = new Rig();
        var token = Rig.Token(cap: 100);
        Assert.Equal(HttpStatusCode.OK, (await rig.Post(token)).StatusCode);    // reads to the end below
        await WaitFor(() => rig.Gateway.Used("run-a") == 150);                   // 120 input + 30 final output; cache reads are not counted, as in the session's own tally

        var refused = await rig.Post(token);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("token budget", await refused.Content.ReadAsStringAsync());
        Assert.Single(rig.Upstream.Calls);
        // Another run's token is unaffected, and a run with no cap is not limited.
        Assert.Equal(HttpStatusCode.OK, (await rig.Post(Rig.Token("run-b", cap: 1000))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.Post(Rig.Token("run-c"))).StatusCode);
        Assert.Contains(rig.Log, e => e.RunId == "run-a" && e.Status == 200 && e.Tokens == 150);
    }

    [Fact]
    public async Task A_json_response_is_counted_from_its_usage_object()
    {
        await using var rig = new Rig(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"m","usage":{"input_tokens":10,"output_tokens":5}}""", Encoding.UTF8, "application/json") });
        var response = await rig.Post(Rig.Token());
        await response.Content.ReadAsStringAsync();
        await WaitFor(() => rig.Gateway.Used("run-a") == 15);
    }

    [Fact]
    public async Task An_unreachable_upstream_is_a_502_that_says_nothing_about_why()
    {
        await using var rig = new Rig(_ => throw new HttpRequestException($"connection refused while sending {Real}"));
        var response = await rig.Post(Rig.Token());
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain(Real, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Neither_the_credential_nor_a_token_nor_any_body_reaches_the_log()
    {
        await using var rig = new Rig();
        var token = Rig.Token();
        await (await rig.Post(token, body: """{"secret-prompt":"do not log me"}""")).Content.ReadAsStringAsync();
        await rig.Post("chm-bad.token");
        await WaitFor(() => rig.Log.Count >= 2);
        var logged = JsonSerializer.Serialize(rig.Log);
        Assert.DoesNotContain(Real, logged);
        Assert.DoesNotContain(token, logged);
        Assert.DoesNotContain("do not log me", logged);
        Assert.DoesNotContain("\"hi\"", logged);
    }

    [Theory]
    [InlineData("1.2.3.4")]
    [InlineData("localhost")]
    [InlineData("")]
    public void The_upstream_must_be_a_public_host_name(string host) =>
        Assert.ThrowsAny<ArgumentException>(() => new ModelGateway(Key, Real, host));

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("a\nb")]
    public void The_credential_must_be_a_single_plain_word(string credential) =>
        Assert.Throws<ArgumentException>(() => new ModelGateway(Key, credential, "api.example.test"));

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }
}

public class ModelTokensTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_token_names_its_run_and_cap_and_checks_without_state()
    {
        var key = ModelTokens.NewKey();
        var grant = ModelTokens.Validate(key, ModelTokens.Mint(key, "run-x.1_b", Now.AddMinutes(10), 5000), Now);
        Assert.Equal(new ModelGrant("run-x.1_b", 5000), grant);
    }

    [Fact]
    public void Another_key_a_tampered_token_an_expired_one_and_garbage_are_refused()
    {
        var key = ModelTokens.NewKey();
        var token = ModelTokens.Mint(key, "run-a", Now.AddMinutes(10), 0);
        Assert.Null(ModelTokens.Validate(ModelTokens.NewKey(), token, Now));
        Assert.Null(ModelTokens.Validate(key, token, Now.AddMinutes(10)));
        Assert.Null(ModelTokens.Validate(key, token[..^2] + (token[^1] == 'A' ? "BB" : "AA"), Now));
        var other = ModelTokens.Mint(key, "run-b", Now.AddMinutes(10), 0);
        Assert.Null(ModelTokens.Validate(key, other.Split('.')[0] + "." + token.Split('.')[1], Now));   // one token's body with another's mac
        foreach (var bad in new[] { null, "", "chm-", "chm-.", "sk-ant-x", new string('x', 600), "chm-!!!.!!!" })
            Assert.Null(ModelTokens.Validate(key, bad, Now));
    }
}

public class UsageScannerTests
{
    private static long Scan(string text, bool sse = true, int chunk = 7)
    {
        var scanner = new UsageScanner(sse);
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var i = 0; i < bytes.Length; i += chunk)
            scanner.Feed(bytes.AsSpan(i, Math.Min(chunk, bytes.Length - i)));
        scanner.Finish();
        return scanner.Tokens;
    }

    [Fact]
    public void A_stream_split_anywhere_gives_the_start_input_plus_the_last_delta_output() =>
        Assert.Equal(150, Scan("data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":120,\"output_tokens\":1}}}\r\n\r\n"
            + "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":12}}\n\ndata: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":30}}"));

    [Fact]
    public void Lines_that_are_not_usage_text_or_json_count_nothing()
    {
        Assert.Equal(0, Scan("event: ping\ndata: not json\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"input_tokens\"}}\n: comment\n"));
        Assert.Equal(0, Scan("data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":\"many\",\"output_tokens\":1.5}}}\n"));
    }

    [Fact]
    public void A_plain_json_body_is_read_at_the_end() =>
        Assert.Equal(15, Scan("""{"usage":{"input_tokens":10,"output_tokens":5}}""", sse: false));

    [Fact]
    public void A_line_longer_than_the_limit_is_skipped_not_buffered()
    {
        var huge = "data: {\"type\":\"message_start\",\"pad\":\"" + new string('x', 2 << 20) + "\"}\n";
        Assert.Equal(7, Scan(huge + "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":7}}\n", chunk: 1 << 16));
    }
}
