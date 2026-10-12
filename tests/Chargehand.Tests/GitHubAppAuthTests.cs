using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>ADR 0046: the integrator acts as a GitHub App. The App's JWT is RS256 and short, the installation token is cached until near its expiry, and neither the key nor a token is ever in a message.</summary>
public class GitHubAppAuthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);

    private static (string Pem, RSA Public) NewKey()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        var publicKey = RSA.Create();
        publicKey.ImportRSAPublicKey(rsa.ExportRSAPublicKey(), out _);
        return (pem, publicKey);
    }

    private static byte[] Decode(string part) => Convert.FromBase64String(part.Replace('-', '+').Replace('_', '/').PadRight((part.Length + 3) / 4 * 4, '='));

    [Fact]
    public void The_jwt_has_the_rs256_header_the_apps_claims_and_a_signature_the_public_key_verifies()
    {
        var (pem, publicKey) = NewKey();
        var jwt = GitHubAppJwt.Create(12345, pem, Now);

        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.DoesNotContain(jwt, c => c is '=' or '+' or '/');
        using var header = JsonDocument.Parse(Decode(parts[0]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());
        using var claims = JsonDocument.Parse(Decode(parts[1]));
        Assert.Equal("12345", claims.RootElement.GetProperty("iss").GetString());
        // GitHub: issued up to a minute in the past, valid for at most ten minutes.
        Assert.Equal(Now.AddSeconds(-60).ToUnixTimeSeconds(), claims.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(Now.AddMinutes(9).ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());
        Assert.True(publicKey.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        // A signature over other bytes does not verify.
        Assert.False(publicKey.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}x"), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void A_key_kept_on_one_line_with_literal_newlines_is_read()
    {
        var (pem, publicKey) = NewKey();
        var jwt = GitHubAppJwt.Create(1, pem.Replace("\n", "\\n", StringComparison.Ordinal), Now);
        var parts = jwt.Split('.');
        Assert.True(publicKey.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void A_key_that_is_not_a_pem_is_refused_without_echoing_it()
    {
        var e = Assert.Throws<GitHubAppException>(() => GitHubAppJwt.Create(1, "not-a-key-super-secret-value", Now));
        Assert.DoesNotContain("super-secret-value", e.Message);
    }

    private sealed class Exchange(HttpStatusCode status = HttpStatusCode.Created) : HttpMessageHandler
    {
        public List<(string Url, string? Auth)> Requests { get; } = [];

        public Func<int, string> Token { get; set; } = n => $"ghs_token{n}";

        public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(1);

        public required Func<DateTimeOffset> Clock { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));
            var body = status == HttpStatusCode.Created
                ? JsonSerializer.Serialize(new { token = Token(Requests.Count), expires_at = Clock().Add(Lifetime).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture) })
                : """{"message":"A JSON web token could not be decoded"}""";
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static (GitHubInstallationTokens Tokens, Exchange Handler, ManualTimeProvider Time, int[] Reads) Source(string pem, HttpStatusCode status = HttpStatusCode.Created)
    {
        var time = new ManualTimeProvider();
        var handler = new Exchange(status) { Clock = time.GetUtcNow };
        var reads = new int[1];
        var tokens = new GitHubInstallationTokens(new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") }, 99, 4242, () => { reads[0]++; return pem; }, time);
        return (tokens, handler, time, reads);
    }

    [Fact]
    public async Task The_token_is_fetched_with_the_apps_jwt_and_kept_until_five_minutes_before_it_expires()
    {
        var (pem, publicKey) = NewKey();
        var (tokens, handler, time, reads) = Source(pem);

        Assert.Equal("ghs_token1", await tokens.GetAsync(default));
        Assert.Equal("ghs_token1", await tokens.GetAsync(default));
        time.Advance(TimeSpan.FromMinutes(54));
        Assert.Equal("ghs_token1", await tokens.GetAsync(default));
        Assert.Single(handler.Requests);
        Assert.Equal(1, reads[0]);

        var (url, auth) = handler.Requests[0];
        Assert.Equal("https://api.example.test/app/installations/4242/access_tokens", url);
        var jwt = auth!["Bearer ".Length..];
        var parts = jwt.Split('.');
        Assert.True(publicKey.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        // Inside the margin the next call replaces it, with a fresh jwt.
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("ghs_token2", await tokens.GetAsync(default));
        Assert.Equal(2, handler.Requests.Count);
        Assert.NotEqual(auth, handler.Requests[1].Auth);
        Assert.Equal(2, reads[0]);
        tokens.Dispose();
    }

    [Fact]
    public async Task A_refusal_names_the_status_and_leaks_neither_the_key_nor_the_jwt()
    {
        var (pem, _) = NewKey();
        var (tokens, handler, _, _) = Source(pem, HttpStatusCode.Unauthorized);

        var e = await Assert.ThrowsAsync<GitHubAppException>(() => tokens.GetAsync(default));

        Assert.Contains("401", e.Message);
        Assert.DoesNotContain("PRIVATE KEY", e.Message);
        Assert.DoesNotContain(handler.Requests[0].Auth!["Bearer ".Length..], e.Message);
        tokens.Dispose();
    }

    [Fact]
    public async Task A_key_the_secret_store_cannot_resolve_is_an_app_exception_without_a_request()
    {
        var time = new ManualTimeProvider();
        var handler = new Exchange { Clock = time.GetUtcNow };
        using var tokens = new GitHubInstallationTokens(new HttpClient(handler), 1, 2, () => throw new InvalidOperationException("no secret source resolved 'app-key'"), time);

        var e = await Assert.ThrowsAsync<GitHubAppException>(() => tokens.GetAsync(default));

        Assert.Contains("app-key", e.Message);
        Assert.Empty(handler.Requests);
    }
}
