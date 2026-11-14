using System.Net;
using Chargehand.Modules.Gateway.Api;

namespace Chargehand.Modules.Research.Internal;

/// <summary>The per-run filtering proxy a sandboxed command is forced to use when <see cref="Api.SandboxSpec.Network"/> is on and
/// <see cref="Api.SandboxSpec.AllowedHosts"/> names a list (issue #905): reuses the egress proxy built for driven-session containers
/// (<see cref="AllowlistProxy"/>, ADR 0039) on a loopback port of its own, rather than a second allowlist engine. A CONNECT to a host
/// not on the list is refused by the proxy itself (<see cref="AllowlistMatcher"/>); see
/// <see cref="Api.BubblewrapSandbox"/>/<see cref="Api.SeatbeltSandbox"/> for how much of "everything else" each platform can actually
/// close off.</summary>
internal sealed class SandboxEgress : IDisposable
{
    private readonly AllowlistProxy _proxy;

    private SandboxEgress(AllowlistProxy proxy, int port)
    {
        _proxy = proxy;
        Port = port;
    }

    internal int Port { get; }

    /// <summary>HTTPS_PROXY/HTTP_PROXY, upper and lower case (curl, npm and most HTTP clients read one or the other; .NET and NuGet
    /// read the upper case), pointed at the proxy; NO_PROXY cleared so nothing can name itself exempt.</summary>
    internal IReadOnlyDictionary<string, string> Env
    {
        get
        {
            var url = $"http://127.0.0.1:{Port}";
            return new Dictionary<string, string>
            {
                ["HTTPS_PROXY"] = url,
                ["https_proxy"] = url,
                ["HTTP_PROXY"] = url,
                ["http_proxy"] = url,
                ["NO_PROXY"] = "",
                ["no_proxy"] = "",
            };
        }
    }

    /// <summary>Starts the proxy on loopback with <paramref name="allowedHosts"/> as its only allowlist entries.</summary>
    internal static SandboxEgress Start(IReadOnlyList<string> allowedHosts)
    {
        var proxy = new AllowlistProxy(new AllowlistMatcher(allowedHosts));
        proxy.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), CancellationToken.None).GetAwaiter().GetResult();
        return new SandboxEgress(proxy, proxy.LocalEndPoint.Port);
    }

    /// <summary>The caller's own values with the proxy's (if any) set on top, so a caller-supplied value never widens the proxy away.</summary>
    internal static IReadOnlyDictionary<string, string> Merge(IReadOnlyDictionary<string, string>? values, SandboxEgress? egress)
    {
        if (egress is null)
            return values ?? new Dictionary<string, string>();
        var merged = new Dictionary<string, string>(values ?? new Dictionary<string, string>());
        foreach (var (name, value) in egress.Env)
            merged[name] = value;
        return merged;
    }

    public void Dispose() => _proxy.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
