using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Chargehand.Modules.Forge.Api;

public sealed class GitHubAppException(string message) : Exception(message);

/// <summary>The credential the integrator acts with: a GitHub App installation access token (ADR 0046). The caller never sees how it is made or when it expires. An App is installed once per account
/// or organisation, so the token is for the installation on the owner of the repository the call is about.</summary>
public interface IInstallationTokens
{
    /// <exception cref="GitHubAppException">The key could not be read, GitHub refused the exchange, or the App is not installed on <paramref name="owner"/>.</exception>
    Task<string> GetAsync(string owner, string repository, CancellationToken ct);
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
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(
            new AppClaims(now.Subtract(Backdate).ToUnixTimeSeconds(), now.Add(Lifetime).ToUnixTimeSeconds(), appId.ToString(CultureInfo.InvariantCulture)), ForgeJsonContext.Default.AppClaims));
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

/// <summary>Finds the App's installation on a repository's owner (<c>GET /repos/{owner}/{repo}/installation</c>, signed with the App's JWT), exchanges the JWT for an installation access token
/// (<c>POST /app/installations/{id}/access_tokens</c>) and keeps both: the installation id per owner for the process, the token until shortly before it expires. The key is read through
/// <paramref name="privateKey"/> only when a request needs it; neither it nor a token is ever in an exception message. An owner the App is not installed on is not remembered, so installing it takes effect
/// at the next call. An installation id that GitHub no longer knows (the App was uninstalled and installed again) is looked up again once.
/// <paramref name="installationId"/> pins one installation, for the account it belongs to (read once from <c>GET /app/installations/{id}</c>): that owner's repositories skip the lookup. It does not
/// reach any other owner, and a pin GitHub does not know is ignored.</summary>
public sealed class GitHubInstallationTokens(HttpClient http, long appId, long? installationId, Func<string> privateKey, TimeProvider time) : IInstallationTokens, IDisposable
{
    /// <summary>A token is replaced when it has less than this left, so a call never starts with one about to lapse.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, long> _installations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, (string Token, DateTimeOffset Expires)> _tokens = [];
    private bool _pinRead;
    private string? _pinAccount;

    public void Dispose() => _gate.Dispose();

    public async Task<string> GetAsync(string owner, string repository, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var looked = false;
            while (true)
            {
                if (!_installations.TryGetValue(owner, out var id))
                {
                    id = await FindInstallationAsync(owner, repository, ct);
                    _installations[owner] = id;
                    looked = true;
                }
                if (_tokens.TryGetValue(id, out var kept) && time.GetUtcNow() < kept.Expires - Margin)
                    return kept.Token;
                var (status, text) = await SendAsync(HttpMethod.Post, $"app/installations/{id.ToString(CultureInfo.InvariantCulture)}/access_tokens", ct);
                if (status is HttpStatusCode.Created or HttpStatusCode.OK)
                    return Remember(id, text);
                if (!looked && status is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
                {
                    // The id kept for this owner is stale: ask GitHub which installation covers it now.
                    _installations.Remove(owner);
                    _tokens.Remove(id);
                    continue;
                }
                throw new GitHubAppException($"GitHub answered {(int)status} to the installation token request: {GitHubApi.Message(text)}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<long> FindInstallationAsync(string owner, string repository, CancellationToken ct)
    {
        if (installationId is { } pinned && string.Equals(await PinnedAccountAsync(pinned, ct), owner, StringComparison.OrdinalIgnoreCase))
            return pinned;
        var (status, text) = await SendAsync(HttpMethod.Get, $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/installation", ct);
        if (status == HttpStatusCode.NotFound)
            throw new GitHubAppException($"the GitHub App is not installed on {owner} (or has no access to {owner}/{repository}); install it there, or set the repository to off");
        if (status != HttpStatusCode.OK)
            throw new GitHubAppException($"GitHub answered {(int)status} to the installation lookup for {owner}/{repository}: {GitHubApi.Message(text)}");
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.GetProperty("id").GetInt64();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new GitHubAppException("GitHub answered the installation lookup with a body that holds no installation id");
        }
    }

    /// <summary>The login of the account the pinned installation belongs to; null when GitHub does not know the installation.</summary>
    private async Task<string?> PinnedAccountAsync(long pinned, CancellationToken ct)
    {
        if (_pinRead)
            return _pinAccount;
        var (status, text) = await SendAsync(HttpMethod.Get, $"app/installations/{pinned.ToString(CultureInfo.InvariantCulture)}", ct);
        if (status == HttpStatusCode.OK)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                _pinAccount = doc.RootElement.GetProperty("account").GetProperty("login").GetString();
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
            }
        }
        else if (status != HttpStatusCode.NotFound)
            throw new GitHubAppException($"GitHub answered {(int)status} to the lookup of installation {pinned}: {GitHubApi.Message(text)}");
        _pinRead = true;
        return _pinAccount;
    }

    private string Remember(long id, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var token = doc.RootElement.GetProperty("token").GetString() ?? throw new KeyNotFoundException();
            _tokens[id] = (token, doc.RootElement.GetProperty("expires_at").GetDateTimeOffset());
            return token;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new GitHubAppException("GitHub answered the installation token request with a body that holds no token");
        }
    }

    /// <summary>A call as the App itself, with a fresh JWT.</summary>
    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, CancellationToken ct)
    {
        string key;
        try
        {
            key = privateKey();
        }
        catch (InvalidOperationException e)
        {
            throw new GitHubAppException($"the GitHub App private key could not be resolved: {e.Message}");
        }
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GitHubAppJwt.Create(appId, key, time.GetUtcNow()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("chargehand", "1"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        try
        {
            using var response = await http.SendAsync(request, ct);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (HttpRequestException e)
        {
            throw new GitHubAppException($"the installation could not be reached: {e.Message}");
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
