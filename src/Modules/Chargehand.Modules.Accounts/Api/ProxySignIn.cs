using System.Net;
using System.Text.RegularExpressions;

namespace Chargehand.Modules.Accounts.Api;

/// <summary>Sign-in by the identity a reverse proxy that does the single sign-on forwards in a header (ADR 0071). The header is plain text, so it counts only
/// on a request for one of <paramref name="Hosts"/> that arrives from one of <paramref name="Proxies"/>.</summary>
/// <param name="Hosts">Host names the proxy serves behind its sign-on; the same proxy may serve other hosts with no sign-on, where the header proves nothing.</param>
/// <param name="Proxies">The proxy's addresses or host names (resolved again every 30 seconds, so a container that restarts under a new address keeps working).</param>
/// <param name="Identities">Email to account: <c>owner</c> or a name in <c>http.accounts</c>.</param>
/// <param name="Header">The header that carries the email; Pangolin's is <c>Remote-Email</c>.</param>
public sealed record ProxySignInSettings(IReadOnlyList<string> Hosts, IReadOnlyList<string> Proxies, IReadOnlyDictionary<string, string> Identities, string Header = "Remote-Email")
{
    public string? AccountOf(string email) =>
        Identities.FirstOrDefault(i => string.Equals(i.Key, email, StringComparison.OrdinalIgnoreCase)).Value;

    public bool ServesHost(string host) => Hosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<string> Problems(IReadOnlyList<AccountSettings> accounts)
    {
        if (Hosts is not { Count: > 0 } || Hosts.Any(h => !Regex.IsMatch(h, @"^[A-Za-z0-9.-]+$")))
            yield return "hosts must list the proxy's host names, with no scheme, port or path";
        if (Proxies is not { Count: > 0 } || Proxies.Any(string.IsNullOrWhiteSpace))
            yield return "proxies must list at least one address or host name the proxy connects from";
        if (string.IsNullOrWhiteSpace(Header))
            yield return "header is required";
        if (Identities is not { Count: > 0 })
            yield return "identities must map at least one email to an account";
        foreach (var (email, account) in Identities ?? new Dictionary<string, string>())
        {
            if (!email.Contains('@', StringComparison.Ordinal))
                yield return $"identities: '{email}' is not an email";
            if (account != AccountSettings.Owner && accounts.All(a => a.Name != account))
                yield return $"identities: '{email}' maps to '{account}', which is neither owner nor in http.accounts";
        }
    }
}

/// <summary>Whether a request came from one of the trusted proxies. Names resolve through <paramref name="resolve"/> and are kept for 30 seconds.</summary>
public sealed class TrustedProxies(IReadOnlyList<string> proxies, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null, TimeProvider? clock = null)
{
    private static readonly TimeSpan Keep = TimeSpan.FromSeconds(30);

    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve = resolve ?? Resolve;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private HashSet<IPAddress> _known = [];
    private DateTimeOffset _at = DateTimeOffset.MinValue;

    public async Task<bool> IncludesAsync(IPAddress? remote, CancellationToken ct)
    {
        if (remote is null)
            return false;
        if (_clock.GetUtcNow() - _at >= Keep)
        {
            var known = new HashSet<IPAddress>();
            foreach (var proxy in proxies)
                foreach (var address in await _resolve(proxy, ct))
                    known.Add(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
            (_known, _at) = (known, _clock.GetUtcNow());
        }
        return _known.Contains(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote);
    }

    private static async Task<IPAddress[]> Resolve(string name, CancellationToken ct)
    {
        if (IPAddress.TryParse(name, out var literal))
            return [literal];
        try
        {
            return await Dns.GetHostAddressesAsync(name, ct);
        }
        catch (System.Net.Sockets.SocketException)
        {
            return [];
        }
    }
}
