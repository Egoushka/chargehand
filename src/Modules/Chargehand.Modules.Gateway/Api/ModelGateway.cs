using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Chargehand.Modules.Gateway.Api;

/// <param name="RunId">The run the token names; null when the token was refused.</param>
/// <param name="Tokens">Input plus output tokens this request counted; never any text.</param>
/// <param name="Fallback">True for a call made on the fallback key (ADR 0088); <paramref name="Usd"/> is what its usage cost at the fallback's prices.</param>
/// <param name="TotalUsd">On a fallback call: the dollars every fallback call of this gateway has cost, this one included. With <paramref name="Calls"/>, <paramref name="Closed"/> and
/// <paramref name="LimitedUntil"/> it makes the last fallback line a summary of the batch, so a log read from its tail still gives the total.</param>
public sealed record GatewayEvent(DateTimeOffset At, string? RunId, int Status, long Tokens, string Reason, bool Fallback = false, decimal Usd = 0, decimal TotalUsd = 0, int Calls = 0, bool Closed = false,
    DateTimeOffset? LimitedUntil = null);

/// <summary>The model endpoint of a batch's egress container (ADR 0039, decision 9): plain HTTP on the internal network, for a session whose <c>ANTHROPIC_BASE_URL</c> points here. It
/// accepts only a request carrying a per-run token (<see cref="ModelTokens"/>), replaces that token with the real credential, which only this process holds, and forwards the request over
/// TLS to the one configured host, following no redirect, and streams the response back. It reads the usage of each response to hold a run to its token cap. It never logs or returns a
/// credential, a header value or any request or response text; what it logs is a run id, a status and token counts. With a <see cref="GatewayFallback"/> it also replays a long
/// subscription limit on the fallback key (ADR 0088).</summary>
public sealed partial class ModelGateway : IAsyncDisposable
{
    private const int MaxRequestBytes = 64 * 1024 * 1024;
    private const int MaxPath = 2048;
    private static readonly TimeSpan UpstreamTimeout = TimeSpan.FromMinutes(15);

    // Headers the gateway sets itself or must not pass either way. Credentials are among them: the session's token never goes upstream, and no upstream header can carry anything back
    // that was not asked for. Accept-Encoding is dropped so the response is not compressed and its usage can be read.
    private static readonly HashSet<string> NotForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "x-api-key", "host", "content-length", "connection", "keep-alive", "transfer-encoding", "te", "trailer", "upgrade", "proxy-authorization",
        "proxy-connection", "expect", "accept-encoding", "cookie", "content-type",
    };

    private readonly string _key;
    private readonly string _credential;
    private readonly Uri _upstream;
    private readonly HttpClient _client;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<GatewayEvent>? _log;
    private readonly ConcurrentDictionary<string, long> _used = new();
    private readonly SemaphoreSlim _connections = new(64);
    private readonly CancellationTokenSource _stop = new();
    private HttpListener? _listener;
    private Task? _accepting;

    /// <param name="hexKey">The batch's key (<see cref="ModelTokens.NewKey"/>).</param>
    /// <param name="credential">The real model credential, held in this object and nowhere else.</param>
    /// <param name="upstreamHost">The one host requests go to, over HTTPS on 443; a name, never an address.</param>
    /// <param name="handler">A test's fake upstream; null: a TLS client that follows no redirect and connects only to public addresses.</param>
    /// <param name="fallback">The API key a long subscription limit is replayed on; null: a limit goes back to the session.</param>
    public ModelGateway(string hexKey, string credential, string upstreamHost, HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null, Action<GatewayEvent>? log = null, GatewayFallback? fallback = null)
    {
        if (fallback is not null)
        {
            if (fallback.Credential.Length == 0 || fallback.Credential.Any(char.IsControl) || fallback.Credential.Any(char.IsWhiteSpace))
                throw new ArgumentException("the fallback credential is empty or has whitespace or control characters", nameof(fallback));
            _fallbackEndpoint = fallback.Endpoint(out var why) ?? throw new ArgumentException($"the fallback endpoint is not acceptable: {why}", nameof(fallback));
            _fallback = fallback;
        }
        if (credential.Length == 0 || credential.Any(char.IsControl) || credential.Any(char.IsWhiteSpace))
            throw new ArgumentException("the model credential is empty or has whitespace or control characters", nameof(credential));
        if (RefuseHost(upstreamHost) is { } reason)
            throw new ArgumentException($"the upstream host is not acceptable: {reason}", nameof(upstreamHost));
        _key = hexKey;
        _credential = credential;
        _upstream = new Uri($"https://{upstreamHost.Trim().TrimEnd('.').ToLowerInvariant()}");
        _client = new HttpClient(handler ?? PublicOnlyHandler(_fallbackEndpoint is { AllowsPrivateAddress: true } e ? e.Host : null), disposeHandler: true) { Timeout = UpstreamTimeout };
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log;
    }

    /// <returns>Why <paramref name="host"/> cannot be the upstream (not a plain public host name: an address, localhost, odd characters); null when it can.</returns>
    public static string? RefuseHost(string host)
    {
        try
        {
            return new AllowlistMatcher([host]).Check(host, 443) is { Allowed: false } refused ? refused.Reason : null;
        }
        catch (ArgumentException e)
        {
            return e.Message;
        }
    }

    /// <summary>Tokens a run has used so far, as counted from the responses.</summary>
    public long Used(string runId) => _used.GetValueOrDefault(runId);

    public void Start(string prefix)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        _accepting = Task.Run(() => AcceptLoop(_stop.Token), CancellationToken.None);
    }

    /// <param name="privateHost">The one host allowed to resolve to a private address: a fallback on plain http (ADR 0088).</param>
    private static SocketsHttpHandler PublicOnlyHandler(string? privateHost) => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseProxy = false,
        UseCookies = false,
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            if (addresses.Length == 0 || (!string.Equals(context.DnsEndPoint.Host, privateHost, StringComparison.OrdinalIgnoreCase) && addresses.Any(a => !AllowlistMatcher.IsPublic(a))))
                throw new HttpRequestException("the upstream name does not resolve to a public address");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    private async Task AcceptLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var context = await _listener!.GetContextAsync();
                await _connections.WaitAsync(ct);
                _ = Task.Run(async () =>
                {
                    try { await Handle(context, ct); }
                    catch (Exception e) when (e is IOException or HttpListenerException or OperationCanceledException or ObjectDisposedException or HttpRequestException) { }
                    finally { context.Response.Abort(); _connections.Release(); }
                }, CancellationToken.None);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or HttpListenerException) { }
    }

    private async Task Handle(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        var grant = ModelTokens.Validate(_key, TokenOf(request), _clock());
        if (grant is null)
        {
            Record(null, 401, 0, "token refused");
            await Refuse(response, 401, "authentication_error", "the run token is not valid or has expired");
            return;
        }
        var path = request.RawUrl ?? "";
        if (request.HttpMethod is not ("POST" or "GET") || !PathAllowed(path))
        {
            Record(grant.RunId, 404, 0, "path or method refused");
            await Refuse(response, 404, "not_found_error", "this endpoint serves the model API only");
            return;
        }
        if (request.ContentLength64 > MaxRequestBytes)
        {
            Record(grant.RunId, 413, 0, "request too large");
            await Refuse(response, 413, "request_too_large", "the request is too large");
            return;
        }
        if (grant.MaxTokens > 0 && Used(grant.RunId) >= grant.MaxTokens)
        {
            // A 4xx that is not 429, so the client does not retry into a cap that will not lift.
            Record(grant.RunId, 403, 0, "token cap reached");
            await Refuse(response, 403, "permission_error", "this run has used its token budget");
            return;
        }

        if (_fallback is not null)
        {
            if (await ReadBody(request, ct) is not { } body)
            {
                Record(grant.RunId, 413, 0, "request too large");
                await Refuse(response, 413, "request_too_large", "the request is too large");
                return;
            }
            await HandleBuffered(request, response, grant, path, body, ct);
            return;
        }

        using var upstream = Upstream(request, path);

        HttpResponseMessage answer;
        try
        {
            answer = await _client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Record(grant.RunId, 502, 0, "the upstream could not be reached");
            await Refuse(response, 502, "api_error", "the model API could not be reached");
            return;
        }
        using (answer)
            await Relay(answer, response, grant.RunId, ct);
    }

    /// <summary>The request to send upstream: the caller's headers except the ones never forwarded, the gateway's credential, and the body.</summary>
    private HttpRequestMessage Upstream(HttpListenerRequest request, string path, byte[]? buffered = null, bool onFallback = false)
    {
        var upstream = new HttpRequestMessage(new HttpMethod(request.HttpMethod), onFallback ? _fallbackEndpoint!.For(path) : new Uri(_upstream, path));
        foreach (string name in request.Headers)
            if (!NotForwarded.Contains(name) && request.Headers.GetValues(name) is { } values)
                CopyHeader(upstream, name, values, onFallback);
        if (onFallback)
            upstream.Headers.TryAddWithoutValidation("x-api-key", _fallback!.Credential);
        else
            upstream.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_credential}");
        if (buffered is not null)
        {
            if (buffered.Length > 0)
            {
                upstream.Content = new ByteArrayContent(buffered);
                if (request.ContentType is { } bufferedType)
                    upstream.Content.Headers.TryAddWithoutValidation("Content-Type", bufferedType);
            }
        }
        else if (request.HasEntityBody)
        {
            var content = new StreamContent(request.InputStream);
            if (request.ContentLength64 >= 0)
                content.Headers.ContentLength = request.ContentLength64;
            if (request.ContentType is { } contentType)
                content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            upstream.Content = content;
        }
        return upstream;
    }

    /// <summary>Streams the upstream answer back and counts its tokens against the run as they pass.</summary>
    private async Task Relay(HttpResponseMessage answer, HttpListenerResponse response, string runId, CancellationToken ct, bool onFallback = false)
    {
        CopyResponseHead(answer, response);
        response.SendChunked = true;
        var scanner = new UsageScanner(serverSentEvents: answer.Content.Headers.ContentType?.MediaType == "text/event-stream");
        long counted = 0;
        await using var body = await answer.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[16 * 1024];
        try
        {
            int n;
            while ((n = await body.ReadAsync(buffer, ct)) > 0)
            {
                scanner.Feed(buffer.AsSpan(0, n));
                var now = scanner.Tokens;
                if (now > counted)
                {
                    Add(runId, now - counted);
                    counted = now;
                }
                await response.OutputStream.WriteAsync(buffer.AsMemory(0, n), ct);
                await response.OutputStream.FlushAsync(ct);
            }
            scanner.Finish();
            if (scanner.Tokens > counted)
            {
                Add(runId, scanner.Tokens - counted);
                counted = scanner.Tokens;
            }
            response.OutputStream.Close();
        }
        finally
        {
            if (onFallback)
                SettleFallbackCall(runId, answer, scanner, counted);
            else
                Record(runId, (int)answer.StatusCode, counted, "");
        }
    }

    private static void CopyResponseHead(HttpResponseMessage answer, HttpListenerResponse response)
    {
        response.StatusCode = (int)answer.StatusCode;
        foreach (var header in answer.Headers.Concat(answer.Content.Headers))
            if (Passes(header.Key))
                response.Headers.Set(header.Key, string.Join(", ", header.Value));
        if (answer.Content.Headers.ContentType is { } type)
            response.ContentType = type.ToString();
    }

    private static string? TokenOf(HttpListenerRequest request)
    {
        var authorization = request.Headers["Authorization"];
        if (authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authorization["Bearer ".Length..].Trim();
        return request.Headers["x-api-key"]?.Trim();
    }

    /// <summary>Only the versioned API (<c>/v1/...</c>), as an absolute path with no dot segment, no authority, no fragment and plain characters.</summary>
    internal static bool PathAllowed(string path) =>
        path.Length is > 4 and <= MaxPath && path.StartsWith("/v1/", StringComparison.Ordinal) && !path.Contains("..", StringComparison.Ordinal) && !path.Contains("//", StringComparison.Ordinal)
        && !path.Contains('#') && !path.Contains('\\') && path.All(c => c is > ' ' and < '\u007f');

    /// <summary>Which upstream response headers go back: the retry hints, the request id and the API's own rate-limit counters, never a redirect, a cookie, anything that sets state,
    /// or an organization, workspace or routing id the upstream (or the fallback gateway) attaches (GitHub issue 663). Content type and length travel through the dedicated
    /// <see cref="HttpListenerResponse"/> properties instead of this allowlist, so they are not named here.</summary>
    private static bool Passes(string name) =>
        name.Equals("Retry-After", StringComparison.OrdinalIgnoreCase) || name.Equals("Request-Id", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("anthropic-ratelimit-", StringComparison.OrdinalIgnoreCase);

    private void Add(string runId, long tokens) => _used.AddOrUpdate(runId, tokens, (_, total) => total + tokens);

    private void Record(string? runId, int status, long tokens, string reason, bool fallback = false, decimal usd = 0, decimal totalUsd = 0, int calls = 0, bool closed = false,
        DateTimeOffset? limitedUntil = null) =>
        _log?.Invoke(new GatewayEvent(_clock(), runId, status, tokens, reason, fallback, usd, totalUsd, calls, closed, limitedUntil));

    private static async Task Refuse(HttpListenerResponse response, int status, string type, string message)
    {
        var body = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new GatewayError("error", new GatewayErrorDetail(type, message)), GatewayJsonContext.Default.GatewayError));
        response.StatusCode = status;
        response.ContentType = "application/json";
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body);
        response.OutputStream.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener?.Close();
        if (_accepting is not null)
            await _accepting;
        _client.Dispose();
        _stop.Dispose();
    }
}
