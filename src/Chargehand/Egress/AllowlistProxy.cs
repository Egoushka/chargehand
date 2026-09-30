using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Chargehand.Egress;

/// <param name="RunId">The run the request says it is for (from Proxy-Authorization); a label for the log, not an identity.</param>
public sealed record EgressEvent(DateTimeOffset At, string? RunId, string Host, int Port, bool Allowed, string Reason);

/// <summary>A CONNECT-only forward proxy with an allowlist (ADR 0039). A session container sits on a network with no route out and is
/// given this proxy as <c>HTTPS_PROXY</c>. It tunnels TLS to a listed host name on an allowed port and to nothing else: no plain HTTP
/// proxying, no IP literal, and no address that is not public after the name is resolved. It sees names, not content: it does not
/// intercept TLS.</summary>
public sealed class AllowlistProxy(AllowlistMatcher matcher, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
    Func<IPAddress, bool>? isPublic = null, Action<EgressEvent>? log = null) : IAsyncDisposable
{
    private const int MaxHeadBytes = 8192;
    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve = resolve ?? ((h, ct) => Dns.GetHostAddressesAsync(h, ct));
    private readonly Func<IPAddress, bool> _isPublic = isPublic ?? AllowlistMatcher.IsPublic;
    private readonly SemaphoreSlim _connections = new(256);
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private Task? _accepting;

    public IPEndPoint LocalEndPoint => (IPEndPoint)(_listener ?? throw new InvalidOperationException("not started")).LocalEndpoint;

    public Task StartAsync(IPEndPoint listen, CancellationToken ct)
    {
        _listener = new TcpListener(listen);
        _listener.Start();
        _accepting = Task.Run(() => AcceptLoop(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                await _connections.WaitAsync(ct);
                _ = Task.Run(async () =>
                {
                    try { await Handle(client, ct); }
                    catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                    finally { client.Dispose(); _connections.Release(); }
                }, CancellationToken.None);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task Handle(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        var (head, extra, status) = await ReadHead(stream, ct);
        if (head is null)
        {
            await Reply(stream, status, ct);
            return;
        }
        var lines = head.Split("\r\n");
        var request = lines[0].Split(' ');
        if (request.Length != 3 || !request[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            await Reply(stream, "400 Bad Request", ct);
            return;
        }
        if (request[0] != "CONNECT")
        {
            await Reply(stream, "405 Method Not Allowed", ct, "Allow: CONNECT\r\n");
            return;
        }
        var runId = RunIdOf(lines);
        var target = request[1];
        var colon = target.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(target[(colon + 1)..], out var port))
        {
            await Reply(stream, "400 Bad Request", ct);
            return;
        }
        var host = target[..colon];
        var decision = matcher.Check(host, port);
        if (!decision.Allowed)
        {
            Record(runId, host, port, false, decision.Reason);
            await Reply(stream, "403 Forbidden", ct);
            return;
        }
        IPAddress[] addresses;
        try { addresses = await _resolve(host, ct); }
        catch (SocketException) { addresses = []; }
        if (addresses.Length == 0)
        {
            Record(runId, host, port, false, "the name did not resolve");
            await Reply(stream, "502 Bad Gateway", ct);
            return;
        }
        if (addresses.Any(a => !_isPublic(a)))
        {
            Record(runId, host, port, false, "the name resolves to a private or reserved address");
            await Reply(stream, "403 Forbidden", ct);
            return;
        }
        using var upstream = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            await upstream.ConnectAsync(addresses, port, timeout.Token);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            Record(runId, host, port, false, "could not connect");
            await Reply(stream, "502 Bad Gateway", ct);
            return;
        }
        Record(runId, host, port, true, "");
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), ct);
        var up = upstream.GetStream();
        if (extra.Length > 0)
            await up.WriteAsync(extra, ct);
        var toUp = stream.CopyToAsync(up, ct);
        var toClient = up.CopyToAsync(stream, ct);
        await Task.WhenAny(toUp, toClient);
    }

    private static async Task<(string? Head, byte[] Extra, string Status)> ReadHead(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeadBytes + 1];
        var length = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HeadTimeout);
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), timeout.Token);
                if (n == 0)
                    return (null, [], "400 Bad Request");
                length += n;
                var end = IndexOfHeadEnd(buffer, length);
                if (end >= 0)
                    return (Encoding.ASCII.GetString(buffer, 0, end), buffer[(end + 4)..length], "");
                if (length > MaxHeadBytes)
                    return (null, [], "431 Request Header Fields Too Large");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, [], "408 Request Timeout");
        }
    }

    private static int IndexOfHeadEnd(byte[] buffer, int length)
    {
        for (var i = 0; i + 3 < length; i++)
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                return i;
        return -1;
    }

    private static string? RunIdOf(string[] lines)
    {
        foreach (var line in lines.Skip(1))
        {
            if (!line.StartsWith("Proxy-Authorization: Basic ", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var user = Encoding.ASCII.GetString(Convert.FromBase64String(line["Proxy-Authorization: Basic ".Length..].Trim())).Split(':')[0];
                return user.Length is > 0 and <= 64 && user.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? user : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }
        return null;
    }

    private void Record(string? runId, string host, int port, bool allowed, string reason) =>
        log?.Invoke(new EgressEvent(DateTimeOffset.UtcNow, runId, host.Length > 253 ? host[..253] : host, port, allowed, reason));

    private static Task Reply(NetworkStream stream, string status, CancellationToken ct, string extraHeaders = "") =>
        stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{extraHeaders}Content-Length: 0\r\nConnection: close\r\n\r\n"), ct).AsTask();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener?.Stop();
        if (_accepting is not null)
            await _accepting;
        _stop.Dispose();
    }
}
