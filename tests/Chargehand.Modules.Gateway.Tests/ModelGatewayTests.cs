using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Chargehand.Modules.Gateway.Api;

namespace Chargehand.Modules.Gateway.Tests;

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
        private readonly List<GatewayEvent> _log = [];
        /// <summary>A copy: the gateway records a request after its response is sent, so a later request's event can land while a test reads.</summary>
        public GatewayEvent[] Log { get { lock (_log) return [.. _log]; } }
        public DateTimeOffset Clock { get; set; } = Now;
        public HttpClient Client { get; } = new();
        public string Url { get; }

        public Rig(Func<HttpRequestMessage, HttpResponseMessage>? answer = null, GatewayFallback? fallback = null)
        {
            Upstream = new FakeUpstream(answer);
            Gateway = new ModelGateway(Key, Real, "api.example.test", Upstream, () => Clock, e => { lock (_log) _log.Add(e); }, fallback);
            // A probed port can be taken by a parallel test before Start binds it: try another.
            for (var attempt = 1; ; attempt++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                Url = $"http://127.0.0.1:{port}";
                try
                {
                    Gateway.Start(Url + "/");
                    break;
                }
                catch (HttpListenerException) when (attempt < 5)
                {
                }
            }
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
    public async Task Only_the_allowlisted_response_headers_reach_the_session()
    {
        await using var rig = new Rig(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"usage":{"input_tokens":1,"output_tokens":1}}""", Encoding.UTF8, "application/json") };
            response.Headers.TryAddWithoutValidation("request-id", "req_1");
            response.Headers.TryAddWithoutValidation("retry-after", "30");
            response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-remaining", "100");
            response.Headers.TryAddWithoutValidation("anthropic-organization-id", "org_123");
            response.Headers.TryAddWithoutValidation("anthropic-workspace-id", "ws_456");
            response.Headers.TryAddWithoutValidation("x-routed-by", "gateway-7");
            response.Headers.TryAddWithoutValidation("set-cookie", "a=b");
            return response;
        });

        var response = await rig.Post(Rig.Token());

        Assert.Equal("req_1", response.Headers.GetValues("request-id").Single());
        Assert.Equal("30", response.Headers.GetValues("retry-after").Single());
        Assert.Equal("100", response.Headers.GetValues("anthropic-ratelimit-unified-remaining").Single());
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.TransferEncodingChunked);   // the non-fallback path always streams, never a Content-Length
        Assert.False(response.Headers.Contains("anthropic-organization-id"));
        Assert.False(response.Headers.Contains("anthropic-workspace-id"));
        Assert.False(response.Headers.Contains("x-routed-by"));
        Assert.False(response.Headers.Contains("set-cookie"));
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
        await WaitFor(() => rig.Log.Any(e => e.RunId == "run-a" && e.Status == 200 && e.Tokens == 150));
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
        await WaitFor(() => rig.Log.Length >= 2);
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

    private const string FallbackKey = "FALLBACK-KEY-do-not-leak-9876543210";
    private const string FallbackHost = "fallback.example.test";
    private const string LimitBody = """{"type":"error","error":{"type":"rate_limit_error","message":"Claude AI usage limit reached"}}""";

    private static string Epoch(TimeSpan fromNow) => (Now + fromNow).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static GatewayFallback Fallback(decimal allowance = 5m) => new(FallbackKey, FallbackHost, allowance, new FallbackPrice(3m, 15m, 0.3m, 3.75m));

    /// <summary>The subscription answers 429 with its limit and a reset <paramref name="resetsIn"/> away; the fallback answers 200 with <paramref name="usage"/>.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> LimitedSubscription(TimeSpan resetsIn, string usage = """{"input_tokens":1000000,"output_tokens":0}""", string limitBody = LimitBody) => request =>
    {
        if (request.RequestUri!.Host == FallbackHost)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"id":"m","usage":{{usage}}}""", Encoding.UTF8, "application/json") };
        var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(limitBody, Encoding.UTF8, "application/json") };
        limited.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-reset", Epoch(resetsIn));
        limited.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-status", "rejected");
        return limited;
    };

    [Fact]
    public async Task A_subscription_limit_is_replayed_on_the_fallback_key_and_the_session_sees_200()
    {
        await using var rig = new Rig(LimitedSubscription(TimeSpan.FromHours(3)), Fallback());
        var body = """{"model":"m","messages":[{"role":"user","content":"hello"}]}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, rig.Url + "/v1/messages?beta=true") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Rig.Token());
        request.Headers.TryAddWithoutValidation("anthropic-beta", "beta-a,oauth-2025-04-20,beta-b");
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        var response = await rig.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"usage\"", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, rig.Upstream.Calls.Count);
        var (subscription, subscriptionBody, _) = rig.Upstream.Calls[0];
        var (replay, replayBody, _) = rig.Upstream.Calls[1];
        Assert.Equal("api.example.test", subscription.RequestUri!.Host);
        Assert.Equal($"Bearer {Real}", subscription.Headers.Authorization!.ToString());
        Assert.Equal(FallbackHost, replay.RequestUri!.Host);
        Assert.Equal("/v1/messages?beta=true", replay.RequestUri.PathAndQuery);
        Assert.Equal(body, replayBody);
        Assert.Equal(subscriptionBody, replayBody);
        Assert.Equal(FallbackKey, replay.Headers.GetValues("x-api-key").Single());
        Assert.Null(replay.Headers.Authorization);
        Assert.Equal("beta-a,beta-b", string.Join(",", replay.Headers.GetValues("anthropic-beta")).Replace(", ", ","));
        Assert.Equal("2023-06-01", replay.Headers.GetValues("anthropic-version").Single());
        Assert.DoesNotContain(Real, string.Join('\n', replay.Headers.Select(h => string.Join(',', h.Value))));
        // One log line for the fallback call, priced from its usage: 1M input tokens at 3 dollars.
        await WaitFor(() => rig.Log.Any(e => e.Fallback));
        var line = rig.Log.Single(e => e.Fallback);
        Assert.Equal((200, 3m), (line.Status, line.Usd));
        Assert.Equal("run-a", line.RunId);
    }

    [Fact]
    public async Task Fallback_log_lines_carry_running_totals_and_the_reset_for_the_server_to_settle()
    {
        await using var rig = new Rig(LimitedSubscription(TimeSpan.FromHours(3), usage: """{"input_tokens":1000000,"output_tokens":0}"""), Fallback(allowance: 10m));
        await (await rig.Post(Rig.Token())).Content.ReadAsStringAsync();
        await WaitFor(() => rig.Log.Count(e => e.Fallback) == 1);
        await (await rig.Post(Rig.Token("run-b"))).Content.ReadAsStringAsync();
        await WaitFor(() => rig.Log.Count(e => e.Fallback) == 2);

        var last = rig.Log.Where(e => e.Fallback).OrderBy(e => e.TotalUsd).Last();

        Assert.Equal((6m, 2, false), (last.TotalUsd, last.Calls, last.Closed));
        Assert.Equal(Now.AddHours(3).ToUnixTimeSeconds(), last.LimitedUntil!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task A_fallback_on_a_private_host_keeps_its_port_and_path_prefix_in_the_replay()
    {
        var fallback = new GatewayFallback(FallbackKey, "bifrost", 5m, new FallbackPrice(3m, 15m, 0.3m, 3.75m), "http://bifrost:8080/anthropic");
        await using var rig = new Rig(request => request.RequestUri!.Host == "bifrost"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"usage":{"input_tokens":1,"output_tokens":1}}""", Encoding.UTF8, "application/json") }
            : LimitedSubscription(TimeSpan.FromHours(3))(request), fallback);

        Assert.Equal(HttpStatusCode.OK, (await rig.Post(Rig.Token())).StatusCode);

        Assert.Equal("http://bifrost:8080/anthropic/v1/messages?beta=true", rig.Upstream.Calls[1].Request.RequestUri!.ToString());
        Assert.Equal(FallbackKey, rig.Upstream.Calls[1].Request.Headers.GetValues("x-api-key").Single());
    }

    [Theory]
    [InlineData("http://gateway.example.test")]
    [InlineData("https://gateway.example.test/a/../b")]
    public void A_fallback_endpoint_that_is_not_acceptable_is_refused_at_construction(string baseUrl) =>
        Assert.Throws<ArgumentException>(() => new ModelGateway(Key, Real, "api.example.test", fallback: new GatewayFallback(FallbackKey, new Uri(baseUrl).Host, 1m, new FallbackPrice(1m, 1m, 1m, 1m), baseUrl)));

    [Fact]
    public async Task Until_the_reset_new_requests_go_straight_to_the_fallback()
    {
        await using var rig = new Rig(LimitedSubscription(TimeSpan.FromHours(3), usage: """{"input_tokens":10,"output_tokens":1}"""), Fallback());
        await (await rig.Post(Rig.Token())).Content.ReadAsStringAsync();
        await (await rig.Post(Rig.Token())).Content.ReadAsStringAsync();

        Assert.Equal(["api.example.test", FallbackHost, FallbackHost], rig.Upstream.Calls.Select(c => c.Request.RequestUri!.Host));
    }

    [Fact]
    public async Task After_the_reset_the_first_request_goes_on_the_subscription()
    {
        var limited = true;
        await using var rig = new Rig(request => limited || request.RequestUri!.Host == FallbackHost
            ? LimitedSubscription(TimeSpan.FromHours(3), usage: """{"input_tokens":10,"output_tokens":1}""")(request)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"usage":{"input_tokens":1,"output_tokens":1}}""", Encoding.UTF8, "application/json") }, Fallback());
        await (await rig.Post(Rig.Token(minutes: 600))).Content.ReadAsStringAsync();
        limited = false;
        rig.Clock = Now.AddHours(3).AddSeconds(1);
        var response = await rig.Post(Rig.Token(minutes: 600));
        await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("api.example.test", rig.Upstream.Calls[^1].Request.RequestUri!.Host);
        Assert.Equal(3, rig.Upstream.Calls.Count);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(30)]
    public async Task A_short_limit_is_not_replayed_because_the_client_retries_it_itself(int seconds)
    {
        await using var rig = new Rig(LimitedSubscription(TimeSpan.FromSeconds(seconds)), Fallback());
        var response = await rig.Post(Rig.Token());
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(LimitBody, await response.Content.ReadAsStringAsync());
        Assert.Single(rig.Upstream.Calls);
    }

    [Fact]
    public async Task A_limit_with_no_reset_is_replayed_only_when_its_body_names_the_usage_limit()
    {
        static Func<HttpRequestMessage, HttpResponseMessage> NoReset(string body) => request => request.RequestUri!.Host == FallbackHost
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"usage":{"input_tokens":1,"output_tokens":1}}""", Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        await using (var usage = new Rig(NoReset(LimitBody), Fallback()))
            Assert.Equal(HttpStatusCode.OK, (await usage.Post(Rig.Token())).StatusCode);
        await using var perMinute = new Rig(NoReset("""{"error":{"type":"rate_limit_error","message":"requests per minute exceeded"}}"""), Fallback());
        Assert.Equal(HttpStatusCode.TooManyRequests, (await perMinute.Post(Rig.Token())).StatusCode);
        Assert.Single(perMinute.Upstream.Calls);
    }

    [Theory]
    [InlineData(402, """{"error":{"type":"budget_error","message":"budget has been exceeded"}}""")]
    [InlineData(429, """{"error":{"type":"rate_limit_error","message":"usage limit reached: the key budget is spent"}}""")]
    public async Task A_budget_refusal_is_passed_through_and_never_replayed(int status, string body)
    {
        await using var rig = new Rig(request =>
        {
            var refused = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            refused.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-reset", Epoch(TimeSpan.FromHours(3)));
            return refused;
        }, Fallback());

        var response = await rig.Post(Rig.Token());

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        Assert.Single(rig.Upstream.Calls);
    }

    [Fact]
    public async Task Past_the_allowance_the_limit_reaches_the_session()
    {
        // 1M input tokens cost 3 dollars; the allowance is 2, so the first replay passes it.
        await using var rig = new Rig(LimitedSubscription(TimeSpan.FromHours(3)), Fallback(allowance: 2m));
        Assert.Equal(HttpStatusCode.OK, (await rig.Post(Rig.Token())).StatusCode);
        await WaitFor(() => rig.Log.Any(e => e.Fallback));

        var limited = await rig.Post(Rig.Token());

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(LimitBody, await limited.Content.ReadAsStringAsync());
        Assert.Equal(1, rig.Upstream.Calls.Count(c => c.Request.RequestUri!.Host == FallbackHost));
    }

    [Fact]
    public async Task A_402_from_the_fallback_closes_it_for_the_rest_of_the_batch()
    {
        await using var rig = new Rig(request => request.RequestUri!.Host == FallbackHost
            ? new HttpResponseMessage(HttpStatusCode.PaymentRequired) { Content = new StringContent("""{"error":{"message":"budget has been exceeded"}}""", Encoding.UTF8, "application/json") }
            : LimitedSubscription(TimeSpan.FromHours(3))(request), Fallback());

        Assert.Equal(HttpStatusCode.PaymentRequired, (await rig.Post(Rig.Token())).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await rig.Post(Rig.Token())).StatusCode);
        Assert.Equal(1, rig.Upstream.Calls.Count(c => c.Request.RequestUri!.Host == FallbackHost));
        Assert.True(rig.Gateway.FallbackClosed);
    }

    [Fact]
    public async Task The_first_subscription_limit_logs_its_status_and_header_names_and_never_a_value_or_the_body()
    {
        await using var rig = new Rig(LimitedSubscription(TimeSpan.FromHours(3)), Fallback());
        await rig.Post(Rig.Token());
        await rig.Post(Rig.Token(), body: """{"secret-prompt":"do not log me"}""");
        await WaitFor(() => rig.Log.Length >= 3);

        var logged = rig.Log.Where(e => e.Status == 429).ToArray();
        var line = Assert.Single(logged);
        Assert.Contains("anthropic-ratelimit-unified-reset", line.Reason);
        var all = JsonSerializer.Serialize(rig.Log);
        Assert.DoesNotContain(Epoch(TimeSpan.FromHours(3)), all);
        Assert.DoesNotContain("usage limit reached", all);
        Assert.DoesNotContain(FallbackKey, all);
        Assert.DoesNotContain(Real, all);
        Assert.DoesNotContain("do not log me", all);
    }

    [Fact]
    public async Task A_request_over_the_buffer_limit_is_refused_when_a_fallback_is_configured()
    {
        await using var rig = new Rig(fallback: Fallback());
        using var content = new StreamContent(new ChunkedZeros(65L * 1024 * 1024));
        // The gateway answers 413 as soon as the body passes the limit and drops the connection, so the client still writing sees either the answer or a reset.
        try
        {
            var response = await rig.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, rig.Url + "/v1/messages") { Content = content, Headers = { Authorization = new AuthenticationHeaderValue("Bearer", Rig.Token()) } });
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        catch (HttpRequestException)   // agent-checks: allow empty-catch -- the reset is the other legal outcome; the 413 log line below proves the refusal
        {
        }
        await WaitFor(() => rig.Log.Any(e => e.Status == 413));
        Assert.Empty(rig.Upstream.Calls);
    }

    /// <summary>The egress container's memory must hold two bodies at the cap at once (issue #664): each is buffered once, at its own size, and relayed once.</summary>
    [Fact]
    public async Task Two_concurrent_maximum_size_buffered_bodies_are_each_relayed_once()
    {
        await using var rig = new Rig(fallback: Fallback());
        const int max = 64 * 1024 * 1024;
        var first = new byte[max];
        var second = new byte[max];
        first[0] = 1;
        second[0] = 2;

        Task<HttpResponseMessage> Send(byte[] body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, rig.Url + "/v1/messages")
            {
                Content = new ByteArrayContent(body),
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", Rig.Token()) },
            };
            return rig.Client.SendAsync(request);
        }

        var responses = await Task.WhenAll(Send(first), Send(second));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(2, rig.Upstream.Calls.Count);
        Assert.Equal(new long?[] { max, max }, rig.Upstream.Calls.Select(c => c.Request.Content!.Headers.ContentLength).OrderBy(l => l));
    }

    /// <summary>The fix (issue #664): a body sent with its length is buffered into a stream sized for exactly that length, so returning it needs no second, full-size copy. Before this,
    /// <c>ReadBody</c> grew a default <see cref="MemoryStream"/> by doubling and then called <c>ToArray()</c>, briefly holding roughly three times the body in memory.</summary>
    [Fact]
    public async Task ReadBody_sized_from_Content_Length_needs_no_extra_full_size_copy()
    {
        const int size = 4 * 1024 * 1024;
        var data = new byte[size];
        new Random(1).NextBytes(data);
        using var input = new MemoryStream(data, writable: false);

        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var body = await ModelGateway.ReadBody(input, hasEntityBody: true, contentLength: size, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(data, body);
        Assert.True(allocated < size * 1.5, $"expected well under {size * 1.5} bytes allocated buffering a {size}-byte body sent with its length, got {allocated}");
    }

    /// <summary>A body of unknown length, so the request goes out chunked and only reading it can tell it is too large.</summary>
    private sealed class ChunkedZeros(long length) : Stream
    {
        private long _left = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, _left);
            Array.Clear(buffer, offset, n);
            _left -= n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

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
        // The last base64url character carries two ignored bits, so tamper with the one before it.
        Assert.Null(ModelTokens.Validate(key, token[..^2] + (token[^2] == 'A' ? 'B' : 'A') + token[^1], Now));
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
