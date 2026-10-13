using System.Globalization;
using Chargehand.Containers;
using Chargehand.Contracts;

namespace Chargehand.Driven;

/// <summary>Where a driven session reaches the model (ADR 0039): an Anthropic-compatible gateway named by <c>driven.network.model_url</c>, which issues the batch a key of its own
/// (a budget, a model list, revocable) instead of the real credential. An <c>http</c> gateway is reached through a forward on the batch's egress container, because the proxy
/// answers CONNECT to port 443 only; an <c>https</c> one goes through the proxy like any host on the allowlist.</summary>
/// <param name="Forward">The egress forward (<c>listen-port=host:port</c>); null for https.</param>
/// <param name="AllowHost">The host the proxy must allow; null for a forward.</param>
/// <param name="BaseUrl">The session's <c>ANTHROPIC_BASE_URL</c>.</param>
public sealed record DrivenModelRoute(string? Forward, string? AllowHost, string BaseUrl)
{
    public bool Forwarded => Forward is not null;

    public int? ForwardPort => Forward is null ? null : int.Parse(Forward[..Forward.IndexOf('=')], CultureInfo.InvariantCulture);

    /// <summary>Null when <paramref name="modelUrl"/> is not set. Throws <see cref="ChargehandException"/> for a URL that cannot work, before anything is created.</summary>
    /// <param name="key">The profile key the URL came from, for the refusals.</param>
    public static DrivenModelRoute? Parse(string? modelUrl, bool priced, int? mcpPort, string key = "driven.network.model_url")
    {
        if (string.IsNullOrEmpty(modelUrl))
            return null;
        if (!priced)
            throw Refuse(key, $"{key} needs claude_code.api_key_secret naming the gateway's key: a subscription token cannot be used through a gateway.");
        if (!Uri.TryCreate(modelUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
            throw Refuse(key, $"{key} must be an absolute http or https URL.");
        if (uri.UserInfo.Length > 0)
            throw Refuse(key, $"{key} carries credentials; put the key in its secret item, not the URL.");
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw Refuse(key, $"{key} has a query or fragment; give the gateway's base path only.");
        var path = uri.AbsolutePath.TrimEnd('/');
        if (uri.Scheme == "https")
            return new DrivenModelRoute(null, uri.Host, $"{uri.GetLeftPart(UriPartial.Authority)}{path}");
        if (uri.Port == mcpPort)
            throw Refuse(key, $"{key} uses port {uri.Port}, which is the chargehand forward's; the two forwards need different ports.");
        var port = uri.Port.ToString(CultureInfo.InvariantCulture);
        return new DrivenModelRoute($"{port}={uri.Host}:{port}", null, $"{BatchNetworkInfo.ServiceUrl(uri.Port)}{path}");
    }

    private static ChargehandException Refuse(string key, string message) =>
        new(ErrorCode.InvalidRequest, message, $"Fix {key}, or remove it to deliver the model credential in the container's environment.");
}
