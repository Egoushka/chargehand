using System.Globalization;
using System.Net;
using System.Text;

namespace Chargehand.Modules.Gateway.Api;

/// <summary>What the fallback key's tokens cost, in dollars per million tokens (the profile's <c>prices</c> entry for the fallback model).</summary>
public sealed record FallbackPrice(decimal InputPerMtok, decimal OutputPerMtok, decimal CacheReadPerMtok, decimal CacheWritePerMtok)
{
    public decimal Usd(long input, long output, long cacheRead, long cacheWrite) =>
        (input * InputPerMtok + output * OutputPerMtok + cacheRead * CacheReadPerMtok + cacheWrite * CacheWritePerMtok) / 1_000_000m;
}

/// <summary>The second credential of a model endpoint (ADR 0088): an API key for <paramref name="Host"/>, the dollars the batch may spend on it, and its prices.</summary>
public sealed record GatewayFallback(string Credential, string Host, decimal AllowanceUsd, FallbackPrice Price, string? BaseUrl = null)
{
    /// <summary>Where the key goes: <see cref="BaseUrl"/>, else <see cref="Host"/> over https. Null with <paramref name="why"/> when it is not acceptable, or when the two name different hosts.</summary>
    public FallbackEndpoint? Endpoint(out string? why)
    {
        var endpoint = FallbackEndpoint.Parse(BaseUrl ?? $"https://{Host}", out why);
        if (endpoint is not null && !string.Equals(endpoint.Host, Host.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
        {
            why = "the base URL names another host";
            return null;
        }
        return endpoint;
    }
}

public sealed partial class ModelGateway
{
    /// <summary>A subscription limit that lifts sooner than this is left to the client's own retry; replaying it would spend money for nothing.</summary>
    private static readonly TimeSpan ShortLimit = TimeSpan.FromMinutes(5);

    /// <summary>How long requests go straight to the fallback when a limit names its usage but not its reset. A wrong guess costs one more 429 from the subscription.</summary>
    private static readonly TimeSpan UnnamedReset = TimeSpan.FromMinutes(15);

    private const int MaxLimitBody = 64 * 1024;

    private readonly GatewayFallback? _fallback;
    private readonly FallbackEndpoint? _fallbackEndpoint;
    private readonly Lock _fallbackState = new();
    private DateTimeOffset _limitedUntil = DateTimeOffset.MinValue;
    private decimal _spent;
    private int _calls;
    private bool _closed;
    private bool _limitShapeLogged;

    /// <summary>Dollars the fallback calls of this batch have cost so far.</summary>
    public decimal FallbackSpent { get { lock (_fallbackState) return _spent; } }

    /// <summary>True once the fallback answered 402: it takes no more requests in this batch.</summary>
    public bool FallbackClosed { get { lock (_fallbackState) return _closed; } }

    private bool FallbackOpen()
    {
        lock (_fallbackState)
            return _fallback is not null && !_closed && _spent < _fallback.AllowanceUsd;
    }

    /// <returns>The body, or null when it is larger than <see cref="MaxRequestBytes"/> (a chunked request has no length to check up front).</returns>
    private static Task<byte[]?> ReadBody(HttpListenerRequest request, CancellationToken ct) =>
        ReadBody(request.InputStream, request.HasEntityBody, request.ContentLength64, ct);

    /// <summary>Buffers <paramref name="input"/> up to <see cref="MaxRequestBytes"/>. Sized from <paramref name="contentLength"/> when it is known and within the limit, so the
    /// common case (a body sent with its length, as every real client does) needs no doubling growth while writing and no extra full-size copy to return it (two maximum-size bodies
    /// in flight at once must not multiply memory past the egress container's limit).</summary>
    internal static async Task<byte[]?> ReadBody(Stream input, bool hasEntityBody, long contentLength, CancellationToken ct)
    {
        if (!hasEntityBody)
            return [];
        using var buffer = contentLength is >= 0 and <= MaxRequestBytes ? new MemoryStream((int)contentLength) : new MemoryStream();
        var chunk = new byte[64 * 1024];
        int n;
        while ((n = await input.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > MaxRequestBytes)
                return null;
            buffer.Write(chunk, 0, n);
        }
        return buffer.TryGetBuffer(out var segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length ? segment.Array : buffer.ToArray();
    }

    /// <summary>A request with a fallback: until a limit's reset it goes to the fallback; otherwise to the subscription, and a long limit from there is sent again on the fallback.</summary>
    private async Task HandleBuffered(HttpListenerRequest request, HttpListenerResponse response, ModelGrant grant, string path, byte[] body, CancellationToken ct)
    {
        bool straightToFallback;
        lock (_fallbackState)
            straightToFallback = _clock() < _limitedUntil;
        if (straightToFallback && FallbackOpen())
        {
            await SendOnFallback(request, response, grant, path, body, ct);
            return;
        }

        HttpResponseMessage answer;
        try
        {
            using var upstream = Upstream(request, path, body);
            answer = await _client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Record(grant.RunId, 502, 0, "the upstream could not be reached");
            await Refuse(response, 502, "api_error", "the model API could not be reached");
            return;
        }
        using (answer)
        {
            if (answer.StatusCode != HttpStatusCode.TooManyRequests)
            {
                await Relay(answer, response, grant.RunId, ct);
                return;
            }
            var text = await ReadLimitBody(answer, ct);
            LogLimitShape(grant.RunId, answer);
            if (FallbackOpen() && SwitchUntil(answer, text) is { } until)
            {
                lock (_fallbackState)
                    _limitedUntil = until;
                await SendOnFallback(request, response, grant, path, body, ct);
                return;
            }
            await RelayBuffered(answer, response, text);
        }
    }

    private async Task SendOnFallback(HttpListenerRequest request, HttpListenerResponse response, ModelGrant grant, string path, byte[] body, CancellationToken ct)
    {
        HttpResponseMessage answer;
        try
        {
            using var upstream = Upstream(request, path, body, onFallback: true);
            answer = await _client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Record(grant.RunId, 502, 0, "the fallback could not be reached", fallback: true);
            await Refuse(response, 502, "api_error", "the model API could not be reached");
            return;
        }
        using (answer)
            await Relay(answer, response, grant.RunId, ct, onFallback: true);
    }

    /// <summary>When the client should start trying the subscription again, or null when this 429 is not one to replay: a budget refusal, a limit that lifts within minutes, a limit that
    /// says nothing of its reset or its usage. The reset comes from the rate-limit header (epoch seconds), else <c>Retry-After</c>.</summary>
    private DateTimeOffset? SwitchUntil(HttpResponseMessage answer, string text)
    {
        if (IsBudget((int)answer.StatusCode, text))
            return null;
        var now = _clock();
        if (ResetOf(answer, now) is { } reset)
            return reset - now > ShortLimit ? reset : null;
        return text.Contains("usage limit", StringComparison.OrdinalIgnoreCase) ? now + UnnamedReset : null;
    }

    /// <summary>The same rule as <c>GatewayRefusal.IsBudget</c> (the sessions module cannot be referenced from here): a 402, or a refusal whose text names a budget.</summary>
    private static bool IsBudget(int status, string text) =>
        status == 402 || text.Contains("budget", StringComparison.OrdinalIgnoreCase) || text.Contains("payment required", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? ResetOf(HttpResponseMessage answer, DateTimeOffset now)
    {
        if (answer.Headers.TryGetValues("anthropic-ratelimit-unified-reset", out var values) && values.FirstOrDefault() is { } value)
        {
            if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) && epoch is > 0 and < 253402300799)
                return DateTimeOffset.FromUnixTimeSeconds(epoch);
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                return at;
        }
        return answer.Headers.RetryAfter switch
        {
            { Delta: { } delta } => now + delta,
            { Date: { } date } => date,
            _ => null,
        };
    }

    private static async Task<string> ReadLimitBody(HttpResponseMessage answer, CancellationToken ct)
    {
        await using var body = await answer.Content.ReadAsStreamAsync(ct);
        var bytes = new byte[MaxLimitBody];
        var read = 0;
        int n;
        while (read < bytes.Length && (n = await body.ReadAsync(bytes.AsMemory(read), ct)) > 0)
            read += n;
        return Encoding.UTF8.GetString(bytes, 0, read);
    }

    private static async Task RelayBuffered(HttpResponseMessage answer, HttpListenerResponse response, string text)
    {
        CopyResponseHead(answer, response);
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.OutputStream.Close();
    }

    /// <summary>On the first subscription 429 the gateway records its status and the NAMES of its rate-limit headers, so the first real limit documents its shape (ADR 0088, unknowns).
    /// Never a value, never the body.</summary>
    private void LogLimitShape(string runId, HttpResponseMessage answer)
    {
        lock (_fallbackState)
        {
            if (_limitShapeLogged)
                return;
            _limitShapeLogged = true;
        }
        var names = answer.Headers.Select(h => h.Key).Where(Passes).Order(StringComparer.OrdinalIgnoreCase);
        Record(runId, (int)answer.StatusCode, 0, $"subscription limit; headers: {string.Join(",", names)}");
    }

    private void SettleFallbackCall(string runId, HttpResponseMessage answer, UsageScanner scanner, long counted)
    {
        var usd = _fallback!.Price.Usd(scanner.Input, scanner.Output, scanner.CacheRead, scanner.CacheWrite);
        decimal total;
        int calls;
        bool closed;
        DateTimeOffset? limitedUntil;
        lock (_fallbackState)
        {
            _spent += usd;
            _calls++;
            if (answer.StatusCode == HttpStatusCode.PaymentRequired)
                _closed = true;
            (total, calls, closed, limitedUntil) = (_spent, _calls, _closed, _limitedUntil == DateTimeOffset.MinValue ? null : _limitedUntil);
        }
        Record(runId, (int)answer.StatusCode, counted, "", fallback: true, usd: usd, totalUsd: total, calls: calls, closed: closed, limitedUntil: limitedUntil);
    }

    /// <summary>Copies a header onto the replayed request. On the fallback, the one value only an OAuth token is accepted with is dropped from <c>anthropic-beta</c>.</summary>
    private static void CopyHeader(HttpRequestMessage upstream, string name, string[] values, bool onFallback)
    {
        if (onFallback && name.Equals("anthropic-beta", StringComparison.OrdinalIgnoreCase))
        {
            var kept = values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Where(v => v != OAuthBeta).ToArray();
            if (kept.Length > 0)
                upstream.Headers.TryAddWithoutValidation(name, string.Join(",", kept));
            return;
        }
        upstream.Headers.TryAddWithoutValidation(name, values);
    }

    private const string OAuthBeta = "oauth-2025-04-20";
}
