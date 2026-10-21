using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chargehand.Modules.Accounts.Api;

/// <summary>Sign-in by the identity Cloudflare Access already checked (ADR 0071): the browser sends no key; Cloudflare adds a signed
/// <c>Cf-Access-Jwt-Assertion</c> header and the server maps its email to an account.</summary>
/// <param name="TeamDomain">The Zero Trust team's host name, <c>&lt;team&gt;.cloudflareaccess.com</c>: the token's issuer and where its signing keys are.</param>
/// <param name="Audience">The Access application's audience tag (AUD), which every token for that application carries.</param>
/// <param name="Identities">Email to account: <c>owner</c> or a name in <c>http.accounts</c>. An email not listed opens nothing.</param>
public sealed record CloudflareAccessSettings(string TeamDomain, string Audience, IReadOnlyDictionary<string, string> Identities)
{
    public const string Header = "Cf-Access-Jwt-Assertion";

    public string Issuer => $"https://{TeamDomain}";

    public Uri CertsUrl => new($"https://{TeamDomain}/cdn-cgi/access/certs");

    /// <summary>The account an email maps to; emails compare without case.</summary>
    public string? AccountOf(string email) =>
        Identities.FirstOrDefault(i => string.Equals(i.Key, email, StringComparison.OrdinalIgnoreCase)).Value;

    public IEnumerable<string> Problems(IReadOnlyList<AccountSettings> accounts)
    {
        if (!Regex.IsMatch(TeamDomain ?? "", @"^[a-z0-9-]+(\.[a-z0-9-]+)+$"))
            yield return "team_domain must be a host name, such as myteam.cloudflareaccess.com, with no scheme or path";
        if (string.IsNullOrWhiteSpace(Audience))
            yield return "audience is required: the Access application's AUD tag";
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

/// <summary>Checks a Cloudflare Access token: RS256 over the team's published keys, this application's audience, the team as issuer, and its time window.
/// Keys are fetched once and again when a token names a key not yet seen, at most once a minute.</summary>
public sealed class CloudflareAccessVerifier(CloudflareAccessSettings settings, Func<CancellationToken, Task<string>> fetchCerts, TimeProvider? clock = null) : IDisposable
{
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RefetchEvery = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _fetch = new(1, 1);
    private Dictionary<string, RSAParameters> _keys = [];
    private DateTimeOffset _fetched = DateTimeOffset.MinValue;

    public void Dispose() => _fetch.Dispose();

    /// <summary>The email of a valid token; null for any token that is not one, whatever the reason.</summary>
    public async Task<string?> EmailAsync(string token, CancellationToken ct)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
            return null;
        try
        {
            using var header = JsonDocument.Parse(Base64Url(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "RS256" || header.RootElement.GetProperty("kid").GetString() is not { } kid)
                return null;
            if (await KeyAsync(kid, ct) is not { } key)
                return null;
            using (var rsa = RSA.Create(key))
                if (!rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    return null;
            using var payload = JsonDocument.Parse(Base64Url(parts[1]));
            var claims = payload.RootElement;
            var now = _clock.GetUtcNow();
            if (claims.GetProperty("iss").GetString() != settings.Issuer || !HasAudience(claims.GetProperty("aud"))
                || DateTimeOffset.FromUnixTimeSeconds(claims.GetProperty("exp").GetInt64()) < now - Skew
                || (claims.TryGetProperty("nbf", out var nbf) && DateTimeOffset.FromUnixTimeSeconds(nbf.GetInt64()) > now + Skew))
                return null;
            return claims.TryGetProperty("email", out var email) && email.GetString() is { Length: > 0 } address ? address : null;
        }
        catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or CryptographicException)
        {
            return null;
        }
    }

    private bool HasAudience(JsonElement aud) => aud.ValueKind switch
    {
        JsonValueKind.String => aud.GetString() == settings.Audience,
        JsonValueKind.Array => aud.EnumerateArray().Any(a => a.GetString() == settings.Audience),
        _ => false,
    };

    private async Task<RSAParameters?> KeyAsync(string kid, CancellationToken ct)
    {
        if (_keys.TryGetValue(kid, out var known))
            return known;
        await _fetch.WaitAsync(ct);
        try
        {
            if (_keys.TryGetValue(kid, out known))
                return known;
            if (_clock.GetUtcNow() - _fetched < RefetchEvery)
                return null;
            _fetched = _clock.GetUtcNow();
            _keys = Parse(await fetchCerts(ct));
            return _keys.TryGetValue(kid, out known) ? known : null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
        finally
        {
            _fetch.Release();
        }
    }

    private static Dictionary<string, RSAParameters> Parse(string certs)
    {
        using var doc = JsonDocument.Parse(certs);
        var keys = new Dictionary<string, RSAParameters>();
        foreach (var key in doc.RootElement.GetProperty("keys").EnumerateArray())
            if (key.TryGetProperty("kty", out var kty) && kty.GetString() == "RSA" && key.GetProperty("kid").GetString() is { } id)
                keys[id] = new RSAParameters { Modulus = Base64Url(key.GetProperty("n").GetString()!), Exponent = Base64Url(key.GetProperty("e").GetString()!) };
        return keys;
    }

    private static byte[] Base64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }
}
