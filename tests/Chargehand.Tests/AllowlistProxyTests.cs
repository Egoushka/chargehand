using System.Net;
using System.Net.Sockets;
using System.Text;
using Chargehand.Egress;

namespace Chargehand.Tests;

/// <summary>The CONNECT-only forward proxy, against a local stub standing in for an allowed host.</summary>
public class AllowlistProxyTests
{
    private static async Task<(AllowlistProxy Proxy, List<EgressEvent> Log, TcpListener Stub)> Start(Func<string, IPAddress[]>? resolve = null)
    {
        var stub = new TcpListener(IPAddress.Loopback, 0);
        stub.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                using var client = await stub.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[64];
                var n = await stream.ReadAsync(buffer);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("echo:" + Encoding.ASCII.GetString(buffer, 0, n)));
            }
        });
        var port = ((IPEndPoint)stub.LocalEndpoint).Port;
        var log = new List<EgressEvent>();
        var proxy = new AllowlistProxy(new AllowlistMatcher(["allowed.test"]) { Ports = [port] },
            (host, _) => Task.FromResult(resolve?.Invoke(host) ?? (host == "allowed.test" ? [IPAddress.Loopback] : [])),
            isPublic: _ => true, log: e => { lock (log) log.Add(e); });
        await proxy.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), default);
        return (proxy, log, stub);
    }

    private static async Task<string> Exchange(IPEndPoint proxy, string head, string? afterConnect = null)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(proxy);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        var buffer = new byte[512];
        var n = await stream.ReadAsync(buffer);
        var reply = Encoding.ASCII.GetString(buffer, 0, n);
        if (afterConnect is not null && reply.StartsWith("HTTP/1.1 200", StringComparison.Ordinal))
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(afterConnect));
            n = await stream.ReadAsync(buffer);
            reply += "|" + Encoding.ASCII.GetString(buffer, 0, n);
        }
        return reply;
    }

    [Fact]
    public async Task A_connect_to_an_allowed_host_reaches_it_and_is_logged_with_the_run_label()
    {
        var (proxy, log, stub) = await Start();
        try
        {
            var port = ((IPEndPoint)stub.LocalEndpoint).Port;
            var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes("run-7:x"));
            var reply = await Exchange(proxy.LocalEndPoint, $"CONNECT allowed.test:{port} HTTP/1.1\r\nHost: allowed.test:{port}\r\nProxy-Authorization: Basic {auth}\r\n\r\n", "hello");
            Assert.StartsWith("HTTP/1.1 200", reply);
            Assert.EndsWith("|echo:hello", reply);
            Assert.Contains(log, e => e is { Allowed: true, Host: "allowed.test", RunId: "run-7" });
        }
        finally { await proxy.DisposeAsync(); stub.Stop(); }
    }

    [Theory]
    [InlineData("other.test", "CONNECT other.test:{port} HTTP/1.1\r\n\r\n", "403")]                // not listed
    [InlineData("allowed.test", "CONNECT allowed.test:9 HTTP/1.1\r\n\r\n", "403")]                  // port not allowed
    [InlineData("1.1.1.1", "CONNECT 1.1.1.1:{port} HTTP/1.1\r\n\r\n", "403")]                       // IP literal
    [InlineData("allowed.test", "GET http://allowed.test/ HTTP/1.1\r\nHost: allowed.test\r\n\r\n", "405")] // plain HTTP proxying
    [InlineData("allowed.test", "POST / HTTP/1.1\r\n\r\n", "405")]
    [InlineData("allowed.test", "garbage\r\n\r\n", "400")]
    public async Task Everything_else_is_refused_and_logged(string name, string head, string status)
    {
        var (proxy, log, stub) = await Start();
        try
        {
            var port = ((IPEndPoint)stub.LocalEndpoint).Port;
            var reply = await Exchange(proxy.LocalEndPoint, head.Replace("{port}", port.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.StartsWith($"HTTP/1.1 {status}", reply);
            Assert.DoesNotContain(log, e => e.Allowed);
            Assert.False(string.IsNullOrEmpty(name));
        }
        finally { await proxy.DisposeAsync(); stub.Stop(); }
    }

    [Fact]
    public async Task A_listed_name_that_resolves_to_a_private_address_is_refused()
    {
        var stub = new TcpListener(IPAddress.Loopback, 0);
        stub.Start();
        var port = ((IPEndPoint)stub.LocalEndpoint).Port;
        var log = new List<EgressEvent>();
        await using var proxy = new AllowlistProxy(new AllowlistMatcher(["rebind.test"]) { Ports = [port] },
            (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Parse("10.0.0.5")]), log: e => log.Add(e));   // default isPublic
        await proxy.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), default);
        var reply = await Exchange(proxy.LocalEndPoint, $"CONNECT rebind.test:{port} HTTP/1.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 403", reply);
        Assert.Contains(log, e => !e.Allowed && e.Reason.Contains("private"));
        stub.Stop();
    }

    [Fact]
    public async Task A_name_that_does_not_resolve_is_a_bad_gateway()
    {
        var (proxy, _, stub) = await Start(_ => []);
        try
        {
            var port = ((IPEndPoint)stub.LocalEndpoint).Port;
            var reply = await Exchange(proxy.LocalEndPoint, $"CONNECT allowed.test:{port} HTTP/1.1\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 502", reply);
        }
        finally { await proxy.DisposeAsync(); stub.Stop(); }
    }

    [Fact]
    public async Task An_oversized_head_is_refused_without_hanging()
    {
        var (proxy, _, stub) = await Start();
        try
        {
            var reply = await Exchange(proxy.LocalEndPoint, "CONNECT allowed.test:1 HTTP/1.1\r\nX: " + new string('a', 20_000) + "\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 431", reply);
        }
        finally { await proxy.DisposeAsync(); stub.Stop(); }
    }
}
