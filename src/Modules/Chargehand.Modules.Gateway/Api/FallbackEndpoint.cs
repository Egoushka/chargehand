using System.Net;
using System.Text.RegularExpressions;

namespace Chargehand.Modules.Gateway.Api;

/// <summary>Where the fallback key goes (ADR 0088): the profile's <c>api_fallback_base_url</c>, or Anthropic. Over <c>https</c> it is a public host name, with any port and path prefix. Over plain
/// <c>http</c> it is only a host that is not on the public internet: a single-label name (a container on the batch's network) or a private or loopback address. The path prefix is put before each
/// replayed request's path. The runner compares the <see cref="Host"/> only.</summary>
/// <param name="Base">Scheme, host and port, with no path.</param>
/// <param name="PathPrefix">Empty, or <c>/segment[/segment]</c> with no trailing slash.</param>
public sealed partial record FallbackEndpoint(Uri Base, string PathPrefix)
{
    public string Host => Base.Host;

    /// <summary>The address a replayed request goes to for <paramref name="pathAndQuery"/> (<c>/v1/...</c>).</summary>
    public Uri For(string pathAndQuery) => new(Base, PathPrefix + pathAndQuery);

    /// <summary>Over <c>http</c> the host may resolve to a private address, which a model endpoint otherwise refuses.</summary>
    public bool AllowsPrivateAddress => Base.Scheme == Uri.UriSchemeHttp;

    /// <summary>The base URL in the form the egress container's env file carries it: scheme, host, port and prefix.</summary>
    public override string ToString() => Base.GetLeftPart(UriPartial.Authority) + PathPrefix;

    /// <param name="baseUrl">The profile's value; null or empty: Anthropic.</param>
    /// <returns>The endpoint, or null with <paramref name="why"/> set.</returns>
    public static FallbackEndpoint? Parse(string? baseUrl, out string? why)
    {
        why = null;
        if (string.IsNullOrWhiteSpace(baseUrl))
            return new FallbackEndpoint(new Uri($"https://{EgressCli.DefaultModelHost}"), "");
        if (PlainPrefix(baseUrl) is { } pathProblem)
        {
            why = pathProblem;
            return null;
        }
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            why = "it must be an http or https URL with no credentials, query or fragment";
            return null;
        }
        var prefix = uri.AbsolutePath.TrimEnd('/');
        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        if (prefix.Length > 0 && !Prefix().IsMatch(prefix))
            why = "its path may hold only plain segments";
        else
            why = HostProblem(uri.Scheme, host);
        return why is null ? new FallbackEndpoint(new Uri($"{uri.Scheme}://{host}" + (uri.IsDefaultPort ? "" : $":{uri.Port}")), prefix) : null;
    }

    /// <summary>Uri folds dot segments away, so a path that tried them is refused before it is parsed.</summary>
    private static string? PlainPrefix(string baseUrl) =>
        baseUrl.Contains("..", StringComparison.Ordinal) || baseUrl.Contains("/./", StringComparison.Ordinal) || baseUrl.Contains('%') ? "its path may hold only plain segments" : null;

    private static string? HostProblem(string scheme, string host) => scheme == Uri.UriSchemeHttps
        ? ModelGateway.RefuseHost(host)
        : PlainHttpHost(host) ? null : "plain http is for a single-label host name or a private or loopback address; use https for any other host";

    private static bool PlainHttpHost(string host) =>
        IPAddress.TryParse(host.Trim('[', ']'), out var address) ? IsPrivate(address) : SingleLabel().IsMatch(host);

    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return true;
        var b = address.GetAddressBytes();
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
            : (b[0] & 0xfe) == 0xfc;
    }

    [GeneratedRegex(@"^(/[A-Za-z0-9._~-]+)+$")]
    private static partial Regex Prefix();

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex SingleLabel();
}
