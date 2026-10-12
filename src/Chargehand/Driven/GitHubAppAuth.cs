using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Chargehand.Driven;

public sealed class GitHubAppException(string message) : Exception(message);

/// <summary>The credential the integrator acts with: a GitHub App installation access token (ADR 0046). The caller never sees how it is made or when it expires.</summary>
public interface IInstallationTokens
{
    /// <exception cref="GitHubAppException">The key could not be read, or GitHub refused the exchange.</exception>
    Task<string> GetAsync(CancellationToken ct);
}

/// <summary>The App's JWT (RS256, GitHub's rules): issued a minute in the past for clock drift, valid for nine minutes (the limit is ten).</summary>
public static class GitHubAppJwt
{
    public static readonly TimeSpan Backdate = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(9);

    /// <exception cref="GitHubAppException">The PEM is not an RSA private key; the message never carries any of it.</exception>
    public static string Create(long appId, string privateKeyPem, DateTimeOffset now)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = now.Subtract(Backdate).ToUnixTimeSeconds(),
            exp = now.Add(Lifetime).ToUnixTimeSeconds(),
            iss = appId.ToString(CultureInfo.InvariantCulture),
        }));
        var signingInput = $"{header}.{claims}";
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(NormalizePem(privateKeyPem));
            return $"{signingInput}.{Base64Url(rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))}";
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            throw new GitHubAppException("the GitHub App private key is not a readable RSA key in PEM form");
        }
    }

    /// <summary>A secret store or an environment variable often holds the PEM on one line with literal <c>\n</c>s; the lines are put back.</summary>
    private static string NormalizePem(string pem) => pem.Contains('\n', StringComparison.Ordinal) ? pem : pem.Replace("\\n", "\n", StringComparison.Ordinal);

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Exchanges the App's JWT for an installation access token (<c>POST /app/installations/{id}/access_tokens</c>) and keeps it until shortly before it expires. The key is read through
/// <paramref name="privateKey"/> only when a new token is needed; neither it nor a token is ever in an exception message.</summary>
public sealed class GitHubInstallationTokens(HttpClient http, long appId, long installationId, Func<string> privateKey, TimeProvider time) : IInstallationTokens, IDisposable
{
    /// <summary>A token is replaced when it has less than this left, so a call never starts with one about to lapse.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;

    public void Dispose() => _gate.Dispose();

    public async Task<string> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && time.GetUtcNow() < _expires - Margin)
                return _token;
            string key;
            try
            {
                key = privateKey();
            }
            catch (InvalidOperationException e)
            {
                throw new GitHubAppException($"the GitHub App private key could not be resolved: {e.Message}");
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, $"app/installations/{installationId.ToString(CultureInfo.InvariantCulture)}/access_tokens");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GitHubAppJwt.Create(appId, key, time.GetUtcNow()));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("chargehand", "1"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException e)
            {
                throw new GitHubAppException($"the installation token could not be fetched: {e.Message}");
            }
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new GitHubAppException($"GitHub answered {(int)response.StatusCode} to the installation token request: {GitHubApi.Message(text)}");
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    _token = doc.RootElement.GetProperty("token").GetString() ?? throw new KeyNotFoundException();
                    _expires = doc.RootElement.GetProperty("expires_at").GetDateTimeOffset();
                    return _token;
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
                {
                    throw new GitHubAppException("GitHub answered the installation token request with a body that holds no token");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>What the GitHub clients share.</summary>
internal static class GitHubApi
{
    /// <summary>The <c>message</c> of an error body, cut; the body itself when it is not JSON.</summary>
    public static string Message(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            return (message ?? "no message").Length > 300 ? message![..300] : message ?? "no message";
        }
        catch (JsonException)
        {
            return body.Length > 200 ? body[..200] : body;
        }
    }
}
