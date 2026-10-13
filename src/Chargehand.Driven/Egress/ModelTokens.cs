using System.Security.Cryptography;
using System.Text;

namespace Chargehand.Egress;

/// <param name="RunId">The run the token was minted for.</param>
/// <param name="MaxTokens">Input plus output tokens the run may use through the gateway; 0 means no cap.</param>
public sealed record ModelGrant(string RunId, long MaxTokens);

/// <summary>The per-run token a session container holds instead of the model credential (ADR 0039, decision 9). It is self-describing and signed: the host mints it with a per-batch
/// key, the egress container holds the same key and checks it without any shared state, so the egress container needs no channel back from the host. A token names one run, an
/// expiry and a token cap, and is worth nothing without the key. Format: <c>chm-</c> + base64url(<c>run:expiry-unix:cap</c>) + <c>.</c> + base64url(HMAC-SHA256 of that part).</summary>
public static class ModelTokens
{
    private const string Prefix = "chm-";

    /// <summary>A fresh per-batch key, hex-encoded for the egress container's environment.</summary>
    public static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static string Mint(string hexKey, string runId, DateTimeOffset expires, long maxTokens)
    {
        var body = Base64Url(Encoding.UTF8.GetBytes($"{runId}:{expires.ToUnixTimeSeconds()}:{Math.Max(0, maxTokens)}"));
        return $"{Prefix}{body}.{Base64Url(Mac(hexKey, body))}";
    }

    /// <returns>The grant, or null for anything that is not a token this key minted, or one past its expiry.</returns>
    public static ModelGrant? Validate(string hexKey, string? token, DateTimeOffset now)
    {
        if (token is null || token.Length > 512 || !token.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        var dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
            return null;
        var body = token[Prefix.Length..dot];
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(Mac(hexKey, body), FromBase64Url(token[(dot + 1)..])))
                return null;
            var parts = Encoding.UTF8.GetString(FromBase64Url(body)).Split(':');
            // The run id never contains a colon (a plain name), so the last two fields are the expiry and the cap.
            if (parts.Length != 3 || !long.TryParse(parts[1], out var expires) || !long.TryParse(parts[2], out var cap) || now.ToUnixTimeSeconds() >= expires)
                return null;
            return new ModelGrant(parts[0], cap);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static byte[] Mac(string hexKey, string body) => HMACSHA256.HashData(Convert.FromHexString(hexKey), Encoding.ASCII.GetBytes(body));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
