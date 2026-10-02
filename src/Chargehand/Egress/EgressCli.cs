using System.Net;
using System.Text.Json;

namespace Chargehand.Egress;

/// <param name="Listen">Where the proxy listens; inside a batch's egress container, 0.0.0.0 on its port.</param>
public sealed record EgressOptions(IPEndPoint Listen, IReadOnlyList<string> Allow, IReadOnlyList<PortForward> Forwards);

/// <summary><c>chargehand egress --listen &lt;ip:port&gt; --allow &lt;host,*.suffix,...&gt;</c>: the allowlist proxy of a batch's egress container
/// (ADR 0039). Profile-free. Each connection decision is one JSON line on stderr, which is what <c>docker logs</c> shows.</summary>
public static class EgressCli
{
    public const string Usage = "usage: chargehand egress --listen <ip:port> --allow <host,*.suffix,...> [--forward <port>=<host>:<port>]...";

    public static EgressOptions? Parse(IReadOnlyList<string> args, TextWriter error)
    {
        IPEndPoint? listen = null;
        List<string>? allow = null;
        List<PortForward> forwards = [];
        for (var i = 0; i < args.Count; i += 2)
        {
            if (i + 1 >= args.Count)
                return Fail(error);
            switch (args[i])
            {
                case "--listen" when IPEndPoint.TryParse(args[i + 1], out var endpoint):
                    listen = endpoint;
                    break;
                case "--forward" when PortForward.Parse(args[i + 1]) is { } forward:
                    forwards.Add(forward);
                    break;
                case "--allow":
                    allow = [.. args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                    break;
                default:
                    return Fail(error);
            }
        }
        if (listen is null || allow is null || allow.Count == 0)
            return Fail(error);
        try
        {
            _ = new AllowlistMatcher(allow);
        }
        catch (ArgumentException e)
        {
            error.WriteLine(e.Message);
            return Fail(error);
        }
        return new EgressOptions(listen, allow, forwards);
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter error, CancellationToken ct)
    {
        if (Parse(args, error) is not { } options)
            return 2;
        await using var proxy = new AllowlistProxy(new AllowlistMatcher(options.Allow), log: e =>
            error.WriteLine(JsonSerializer.Serialize(new { at = e.At, run = e.RunId, host = e.Host, port = e.Port, allowed = e.Allowed, reason = e.Reason })));
        await proxy.StartAsync(options.Listen, ct);
        var forwards = options.Forwards.Select(f => new TcpForward(f)).ToList();
        try
        {
            foreach (var forward in forwards)
                await forward.StartAsync(ct);
            await Run(proxy, error, ct);
        }
        finally
        {
            foreach (var forward in forwards)
                await forward.DisposeAsync();
        }
        return 0;
    }

    private static async Task Run(AllowlistProxy proxy, TextWriter error, CancellationToken ct)
    {
        error.WriteLine($"egress proxy listening on {proxy.LocalEndPoint}");
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
    }

    private static EgressOptions? Fail(TextWriter error)
    {
        error.WriteLine(Usage);
        return null;
    }
}
