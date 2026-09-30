using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Chargehand.Egress;

/// <param name="Allowed">Whether the connection may go ahead.</param>
/// <param name="Reason">Why not, in words; empty when allowed.</param>
public readonly record struct EgressDecision(bool Allowed, string Reason);

/// <summary>Which hosts and ports a session container may reach through the egress proxy (ADR 0039), and which addresses no entry can
/// unlock. An entry is a host name (exact), <c>*.suffix</c> or <c>.suffix</c> (subdomains, not the apex). IP literals, <c>localhost</c>
/// and non-ASCII names are refused even when listed: the list is for public registries and APIs, and a name is checked again after it
/// resolves (<see cref="IsPublic"/>).</summary>
public sealed partial class AllowlistMatcher
{
    private readonly HashSet<string> _exact = new(StringComparer.Ordinal);
    private readonly List<string> _suffixes = [];

    public AllowlistMatcher(IEnumerable<string> entries)
    {
        foreach (var raw in entries)
        {
            var entry = Normalize(raw);
            if (entry.StartsWith("*.", StringComparison.Ordinal) || entry.StartsWith('.'))
            {
                var suffix = entry.TrimStart('*').TrimStart('.');
                if (!HostPattern().IsMatch(suffix) || !suffix.Contains('.'))
                    throw new ArgumentException($"allowlist entry '{raw}' is not a host suffix pattern (*.example.com)", nameof(entries));
                _suffixes.Add("." + suffix);
            }
            else
            {
                if (!HostPattern().IsMatch(entry))
                    throw new ArgumentException($"allowlist entry '{raw}' is not a host name", nameof(entries));
                _exact.Add(entry);
            }
        }
        if (_exact.Count == 0 && _suffixes.Count == 0)
            throw new ArgumentException("an allowlist needs at least one entry", nameof(entries));
    }

    /// <summary>Ports a connection may name. Default 443 only; a test may set another.</summary>
    public IReadOnlyList<int> Ports { get; init; } = [443];

    public EgressDecision Check(string host, int port)
    {
        var name = Normalize(host);
        if (name.Length == 0 || !name.All(c => c < 128 && c > 32))
            return new(false, "the host name is empty or not plain ASCII");
        if (IPAddress.TryParse(name.Trim('[', ']'), out _))
            return new(false, "an IP literal is not allowed; use a listed host name");
        if (name == "localhost" || name.EndsWith(".localhost", StringComparison.Ordinal))
            return new(false, "localhost is not allowed");
        if (!Ports.Contains(port))
            return new(false, $"port {port} is not allowed (only {string.Join(", ", Ports)})");
        if (_exact.Contains(name) || _suffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal) && name.Length > s.Length))
            return new(true, "");
        return new(false, $"'{name}' is not on the allowlist");
    }

    /// <summary>False for every address a listed name must never reach: loopback, private, link-local (cloud metadata), carrier-grade NAT
    /// (also tailnets), unspecified, multicast and broadcast, for IPv4 and IPv6 alike, and IPv4-mapped IPv6 forms of them.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast))
            return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] is >= 64 and <= 127)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || b[0] >= 224);
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return !((b[0] & 0xfe) == 0xfc                       // fc00::/7 unique local
                || (b[0] == 0xfe && (b[1] & 0xc0) == 0x80)       // fe80::/10 link-local
                || b[0] == 0xff);                                // multicast
        return false;
    }

    private static string Normalize(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$")]
    private static partial Regex HostPattern();
}
