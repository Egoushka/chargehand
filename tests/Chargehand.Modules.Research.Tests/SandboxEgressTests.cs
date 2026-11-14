using System.Net;
using System.Net.Sockets;
using System.Text;
using Chargehand.Modules.Research.Internal;

namespace Chargehand.Modules.Research.Tests;

/// <summary>The loopback allowlist proxy a sandboxed command is forced to use once a host list is set (issue #905).</summary>
public class SandboxEgressTests
{
    [Fact]
    public void Env_points_both_cases_of_the_proxy_variables_at_the_loopback_port_and_clears_no_proxy()
    {
        using var egress = SandboxEgress.Start(["allowed.test"]);

        var url = $"http://127.0.0.1:{egress.Port}";
        Assert.Equal(url, egress.Env["HTTPS_PROXY"]);
        Assert.Equal(url, egress.Env["https_proxy"]);
        Assert.Equal(url, egress.Env["HTTP_PROXY"]);
        Assert.Equal(url, egress.Env["http_proxy"]);
        Assert.Equal("", egress.Env["NO_PROXY"]);
        Assert.Equal("", egress.Env["no_proxy"]);
    }

    [Fact]
    public async Task A_connect_to_a_host_not_on_the_list_is_refused()
    {
        using var egress = SandboxEgress.Start(["allowed.test"]);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, egress.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("CONNECT other.test:443 HTTP/1.1\r\n\r\n"));
        var buffer = new byte[256];
        var n = await stream.ReadAsync(buffer);

        Assert.StartsWith("HTTP/1.1 403", Encoding.ASCII.GetString(buffer, 0, n), StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_keeps_the_callers_values_but_the_proxy_variables_always_win()
    {
        using var egress = SandboxEgress.Start(["allowed.test"]);
        var callerProxy = new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://widen.test:1", ["OTHER"] = "kept" };

        var merged = SandboxEgress.Merge(callerProxy, egress);

        Assert.Equal($"http://127.0.0.1:{egress.Port}", merged["HTTPS_PROXY"]);
        Assert.Equal("kept", merged["OTHER"]);
    }

    [Fact]
    public void Merge_with_no_egress_returns_the_callers_values_unchanged()
    {
        var values = new Dictionary<string, string> { ["A"] = "1" };

        Assert.Same(values, SandboxEgress.Merge(values, null));
        Assert.Empty(SandboxEgress.Merge(null, null));
    }

    [Fact]
    public async Task Dispose_stops_the_proxy_so_a_later_connection_is_refused()
    {
        var egress = SandboxEgress.Start(["allowed.test"]);
        var port = egress.Port;
        egress.Dispose();

        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port));
    }
}
