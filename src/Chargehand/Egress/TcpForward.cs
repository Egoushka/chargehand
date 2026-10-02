using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Chargehand.Egress;

/// <summary>A fixed TCP forward of a batch's egress container (ADR 0039): connections to <see cref="ListenPort"/> on its internal side go to <see cref="Host"/>:<see cref="Port"/>.
/// It is how a session reaches the chargehand server's MCP endpoint, which sits on a private address the allowlist proxy would refuse. The operator names each forward when the
/// batch's network is made; nothing a session sends can add one or change a target.</summary>
public sealed partial record PortForward(int ListenPort, string Host, int Port)
{
    /// <summary>The proxy's own default port, which no forward may take.</summary>
    public const int ProxyPort = 3128;

    /// <returns>Null unless <paramref name="text"/> is <c>listen-port=host:port</c> with a named host (no address literal), a listen port of 1024 or more that is not the proxy's, and a valid target port.</returns>
    public static PortForward? Parse(string text)
    {
        var m = Pattern().Match(text);
        if (!m.Success || !int.TryParse(m.Groups["l"].Value, out var listen) || !int.TryParse(m.Groups["p"].Value, out var port))
            return null;
        var host = m.Groups["h"].Value;
        if (listen is < 1024 or > 65535 || listen == ProxyPort || port is < 1 or > 65535 || IPAddress.TryParse(host, out _))
            return null;
        return new PortForward(listen, host, port);
    }

    [GeneratedRegex(@"^(?<l>\d{1,5})=(?<h>[a-z0-9]([a-z0-9.-]*[a-z0-9])?):(?<p>\d{1,5})$")]
    private static partial Regex Pattern();
}

public sealed class TcpForward(PortForward forward, IPAddress? bind = null) : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private Task? _accepting;

    public int LocalPort => ((IPEndPoint)(_listener ?? throw new InvalidOperationException("not started")).LocalEndpoint).Port;

    public Task StartAsync(CancellationToken ct)
    {
        _listener = new TcpListener(bind ?? IPAddress.Any, forward.ListenPort);
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
                _ = Task.Run(async () =>
                {
                    try { await Splice(client, ct); }
                    catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                    finally { client.Dispose(); }
                }, CancellationToken.None);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task Splice(TcpClient client, CancellationToken ct)
    {
        using var upstream = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        await upstream.ConnectAsync(forward.Host, forward.Port, timeout.Token);
        var down = client.GetStream();
        var up = upstream.GetStream();
        await Task.WhenAny(down.CopyToAsync(up, ct), up.CopyToAsync(down, ct));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener?.Stop();
        if (_accepting is not null)
            await _accepting;
        _stop.Dispose();
    }
}
