using System.Net;
using System.Net.Sockets;
using System.Text;
using Chargehand.Egress;

namespace Chargehand.Tests;

/// <summary>ADR 0039: how a session on the batch network reaches the chargehand server. The operator names each forward (a port and a target); nothing a session sends can choose one.</summary>
public class TcpForwardTests
{
    private static (TcpListener Stub, int Port) Stub()
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
                await stream.WriteAsync(Encoding.ASCII.GetBytes("stub:" + Encoding.ASCII.GetString(buffer, 0, n)));
            }
        });
        return (stub, ((IPEndPoint)stub.LocalEndpoint).Port);
    }

    [Fact]
    public async Task Bytes_go_to_the_named_target_and_back()
    {
        var (stub, port) = Stub();
        await using var forward = new TcpForward(new PortForward(0, "127.0.0.1", port), IPAddress.Loopback);
        await forward.StartAsync(default);
        using var client = new TcpClient();
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, forward.LocalPort));
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("hello"));
        var buffer = new byte[64];
        var n = await stream.ReadAsync(buffer);
        Assert.Equal("stub:hello", Encoding.ASCII.GetString(buffer, 0, n));
        stub.Stop();
    }

    [Fact]
    public async Task A_target_that_is_down_closes_the_client_without_hanging()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var deadPort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        await using var forward = new TcpForward(new PortForward(0, "127.0.0.1", deadPort), IPAddress.Loopback);
        await forward.StartAsync(default);
        using var client = new TcpClient();
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, forward.LocalPort));
        var n = await client.GetStream().ReadAsync(new byte[8]).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, n);
    }

    [Theory]
    [InlineData("4301=chargehand:4300", 4301, "chargehand", 4300)]
    [InlineData("4301=chargehand-server.internal:4300", 4301, "chargehand-server.internal", 4300)]
    public void A_forward_parses(string text, int listen, string host, int port) => Assert.Equal(new PortForward(listen, host, port), PortForward.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("4301")]
    [InlineData("4301=host")]
    [InlineData("80=host:4300")]                  // a privileged port
    [InlineData("4301=host:0")]
    [InlineData("4301=host:70000")]
    [InlineData("4301=1.1.1.1:80")]               // a forward is to a named service, not an address
    [InlineData("4301=a b:80")]
    [InlineData("3128=host:80")]                  // the proxy's own port
    public void A_bad_forward_is_refused(string text) => Assert.Null(PortForward.Parse(text));
}
