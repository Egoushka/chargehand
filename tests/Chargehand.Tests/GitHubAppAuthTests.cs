using System.Globalization;
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

    /// <summary>GitHub as an App sees it: installations on some owners, each with an account, and tokens that expire.</summary>
    private sealed class FakeApp(ManualTimeProvider time) : HttpMessageHandler
    {
        /// <summary>Owner to the id of the App's installation there; an owner not in it has no installation.</summary>
        public Dictionary<string, long> Installations { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<long> Known { get; } = [];

        public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(1);

        public HttpStatusCode? LookupAnswers { get; set; }

        public List<(HttpMethod Method, string Path, string? Auth)> Requests { get; } = [];

        public int Calls(string path) => Requests.Count(r => r.Path == path);

        public void Install(string owner, long id)
        {
            Installations[owner] = id;
            Known.Add(id);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method, path, request.Headers.Authorization?.ToString()));
            var parts = path.Trim('/').Split('/');
            HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };
            HttpResponseMessage NotFound() => Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound);
            if (LookupAnswers is { } fixedStatus && parts is ["repos", _, _, "installation"])
                return Task.FromResult(Json("""{"message":"A JSON web token could not be decoded"}""", fixedStatus));
            if (parts is ["repos", var owner, _, "installation"])
                return Task.FromResult(Installations.TryGetValue(owner, out var id) ? Json($$$"""{"id":{{{id}}},"account":{"login":"{{{owner}}}"}}""") : NotFound());
            if (parts is ["app", "installations", var pinned] && long.TryParse(pinned, out var known) && Known.Contains(known))
                return Task.FromResult(Json($$$"""{"id":{{{known}}},"account":{"login":"{{{Installations.FirstOrDefault(i => i.Value == known).Key}}}"}}"""));
            if (parts is ["app", "installations", var target, "access_tokens"] && long.TryParse(target, out var exchanged) && Known.Contains(exchanged))
            {
                var issued = Requests.Count(r => r.Path.EndsWith("access_tokens", StringComparison.Ordinal));
                var expires = time.GetUtcNow().Add(Lifetime).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                return Task.FromResult(Json($$"""{"token":"ghs_{{exchanged}}_{{issued}}","expires_at":"{{expires}}"}""", HttpStatusCode.Created));
            }
            return Task.FromResult(NotFound());
        }
    }

    private static (GitHubInstallationTokens Tokens, FakeApp App, ManualTimeProvider Time, int[] Reads) Source(string pem, long? pinned = null)
    {
        var time = new ManualTimeProvider();
        var app = new FakeApp(time);
        var reads = new int[1];
        return (new GitHubInstallationTokens(new HttpClient(app) { BaseAddress = new Uri("https://api.example.test/") }, 99, pinned, () => { reads[0]++; return pem; }, time), app, time, reads);
    }

    [Fact]
    public async Task The_installation_is_looked_up_for_the_owner_with_the_apps_jwt_and_the_token_is_kept_until_five_minutes_before_it_expires()
    {
        var (pem, publicKey) = NewKey();
        var (tokens, app, time, _) = Source(pem);
        app.Install("alice", 4242);

        Assert.Equal("ghs_4242_1", await tokens.GetAsync("alice", "repo", default));
        Assert.Equal("ghs_4242_1", await tokens.GetAsync("alice", "repo", default));
        // Another repository of the same owner is the same installation: no new lookup.
        Assert.Equal("ghs_4242_1", await tokens.GetAsync("Alice", "other", default));
        time.Advance(TimeSpan.FromMinutes(54));
        Assert.Equal("ghs_4242_1", await tokens.GetAsync("alice", "repo", default));
        Assert.Equal([(HttpMethod.Get, "/repos/alice/repo/installation"), (HttpMethod.Post, "/app/installations/4242/access_tokens")], app.Requests.Select(r => (r.Method, r.Path)));

        // Both calls were made as the App: the JWT verifies with the public key.
        foreach (var (_, _, auth) in app.Requests)
        {
            var parts = auth!["Bearer ".Length..].Split('.');
            Assert.True(publicKey.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }

        // Inside the margin the next call replaces the token, with a fresh jwt, and keeps the installation.
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("ghs_4242_2", await tokens.GetAsync("alice", "repo", default));
        Assert.Equal(1, app.Calls("/repos/alice/repo/installation"));
        Assert.NotEqual(app.Requests[0].Auth, app.Requests[^1].Auth);
        tokens.Dispose();
    }

    [Fact]
    public async Task Each_owner_gets_the_installation_the_app_has_there()
    {
        var (pem, _) = NewKey();
        var (tokens, app, _, _) = Source(pem);
        app.Install("alice", 1);
        app.Install("acme-org", 2);

        Assert.Equal("ghs_1_1", await tokens.GetAsync("alice", "chargehand", default));
        Assert.Equal("ghs_2_2", await tokens.GetAsync("acme-org", "mobile", default));
        Assert.Equal("ghs_1_1", await tokens.GetAsync("alice", "jarvis", default));
        Assert.Equal(["/repos/alice/chargehand/installation", "/app/installations/1/access_tokens", "/repos/acme-org/mobile/installation", "/app/installations/2/access_tokens"], app.Requests.Select(r => r.Path));
        tokens.Dispose();
    }

    [Fact]
    public async Task An_owner_without_the_app_is_a_clear_error_and_is_asked_again_once_the_app_is_installed()
    {
        var (pem, _) = NewKey();
        var (tokens, app, _, _) = Source(pem);
        app.Install("alice", 1);

        var e = await Assert.ThrowsAsync<GitHubAppException>(() => tokens.GetAsync("acme-org", "mobile", default));
        Assert.Contains("the GitHub App is not installed on acme-org", e.Message);
        Assert.Contains("set the repository to off", e.Message);
        Assert.DoesNotContain("Resource not accessible", e.Message);
        // The other owner is not affected.
        Assert.Equal("ghs_1_1", await tokens.GetAsync("alice", "chargehand", default));

        // The organisation installs the App: no restart needed.
        app.Install("acme-org", 2);
        Assert.Equal("ghs_2_2", await tokens.GetAsync("acme-org", "mobile", default));
        tokens.Dispose();
    }

    [Fact]
    public async Task An_installation_id_that_github_forgot_is_looked_up_again_once()
    {
        var (pem, _) = NewKey();
        var (tokens, app, time, _) = Source(pem);
        app.Install("alice", 1);
        Assert.Equal("ghs_1_1", await tokens.GetAsync("alice", "repo", default));

        // The App is uninstalled and installed again: a new id, and the old one answers 404 to the exchange.
        app.Known.Remove(1);
        app.Install("alice", 5);
        time.Advance(TimeSpan.FromMinutes(58));
        Assert.Equal("ghs_5_3", await tokens.GetAsync("alice", "repo", default));
        Assert.Equal(2, app.Calls("/repos/alice/repo/installation"));

        // An id that is wrong right after a lookup is not looked up in a loop.
        var (second, gone, _, _) = Source(pem);
        gone.Installations["bob"] = 9;
        var e = await Assert.ThrowsAsync<GitHubAppException>(() => second.GetAsync("bob", "repo", default));
        Assert.Contains("404", e.Message);
        Assert.Equal(1, gone.Calls("/repos/bob/repo/installation"));
        tokens.Dispose();
        second.Dispose();
    }

    [Fact]
    public async Task A_pinned_installation_serves_the_account_it_belongs_to_without_a_lookup_and_no_other_owner()
    {
        var (pem, _) = NewKey();
        var (tokens, app, _, _) = Source(pem, pinned: 4242);
        app.Install("alice", 4242);
        app.Install("acme-org", 2);

        Assert.Equal("ghs_4242_1", await tokens.GetAsync("alice", "chargehand", default));
        Assert.Equal(0, app.Calls("/repos/alice/chargehand/installation"));
        Assert.Equal(1, app.Calls("/app/installations/4242"));
        Assert.Equal("ghs_2_2", await tokens.GetAsync("acme-org", "mobile", default));
        Assert.Equal(1, app.Calls("/app/installations/4242"));

        var (bare, _, _, _) = Source(pem, pinned: 7);
        var e = await Assert.ThrowsAsync<GitHubAppException>(() => bare.GetAsync("acme-org", "mobile", default));
        Assert.Contains("not installed on acme-org", e.Message);
        tokens.Dispose();
        bare.Dispose();
    }

    [Fact]
    public async Task A_pinned_installation_that_github_does_not_know_is_ignored()
    {
        var (pem, _) = NewKey();
        var (tokens, app, _, _) = Source(pem, pinned: 4242);
        app.Install("alice", 8);

        Assert.Equal("ghs_8_1", await tokens.GetAsync("alice", "chargehand", default));
        Assert.Equal(1, app.Calls("/repos/alice/chargehand/installation"));
        tokens.Dispose();
    }

    [Fact]
    public async Task A_refusal_names_the_status_and_leaks_neither_the_key_nor_the_jwt()
    {
        var (pem, _) = NewKey();
        var (tokens, app, _, _) = Source(pem);
        app.LookupAnswers = HttpStatusCode.Unauthorized;

        var e = await Assert.ThrowsAsync<GitHubAppException>(() => tokens.GetAsync("alice", "repo", default));

        Assert.Contains("401", e.Message);
        Assert.DoesNotContain("PRIVATE KEY", e.Message);
        Assert.DoesNotContain(app.Requests[0].Auth!["Bearer ".Length..], e.Message);
        tokens.Dispose();
    }

    [Fact]
    public async Task A_key_the_secret_store_cannot_resolve_is_an_app_exception_without_a_request()
    {
        var time = new ManualTimeProvider();
        var app = new FakeApp(time);
        using var tokens = new GitHubInstallationTokens(new HttpClient(app), 1, null, () => throw new InvalidOperationException("no secret source resolved 'app-key'"), time);

        var e = await Assert.ThrowsAsync<GitHubAppException>(() => tokens.GetAsync("alice", "repo", default));

        Assert.Contains("app-key", e.Message);
        Assert.Empty(app.Requests);
    }
}
