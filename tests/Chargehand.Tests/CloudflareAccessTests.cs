using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>Sign-in by the Cloudflare Access identity (ADR 0071): a token the team signed, for this application, maps its email to an account;
/// anything else opens nothing, and a key still decides when one is sent.</summary>
public class CloudflareAccessTests
{
    private const string Team = "team.example.test";
    private const string Aud = "aud-of-the-console";

    private static readonly RSA Signer = RSA.Create(2048);

    private static readonly CloudflareAccessSettings Access = new(Team, Aud, new Dictionary<string, string>
    {
        ["Owner@Example.test"] = "owner",
        ["alice@example.test"] = "alice",
    });

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Certs(RSA key, string kid = "k1")
    {
        var p = key.ExportParameters(false);
        return JsonSerializer.Serialize(new { keys = new[] { new { kid, kty = "RSA", alg = "RS256", n = B64(p.Modulus!), e = B64(p.Exponent!) } } });
    }

    private static string Token(string email, string aud = Aud, string iss = $"https://{Team}", TimeSpan? expiresIn = null, RSA? key = null, string kid = "k1")
    {
        var header = B64(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg = "RS256", kid, typ = "JWT" })));
        var exp = DateTimeOffset.UtcNow.Add(expiresIn ?? TimeSpan.FromHours(1)).ToUnixTimeSeconds();
        var payload = B64(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { aud = new[] { aud }, email, exp, iat = exp - 3600, iss, sub = "x" })));
        var signature = (key ?? Signer).SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{B64(signature)}";
    }

    private static Task<TestServer> Start(Func<CancellationToken, Task<string>>? certs = null) =>
        TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [new AccountSettings("alice", AllowServerCredential: true)],
            tweak: (_, s) => s with { Access = Access, AccessCerts = certs ?? (_ => Task.FromResult(Certs(Signer))) });

    private static async Task<HttpResponseMessage> Me(TestServer s, string? token, bool console = true, string? bearer = null)
    {
        using var http = new HttpClient { BaseAddress = s.BaseAddress };
        var get = new HttpRequestMessage(HttpMethod.Get, "/v1/accounts/me");
        if (token is not null)
            get.Headers.Add(CloudflareAccessSettings.Header, token);
        if (console)
            get.Headers.Add(ChargehandServer.ConsoleHeader, "1");
        if (bearer is not null)
            get.Headers.Authorization = new("Bearer", bearer);
        return await http.SendAsync(get);
    }

    [Fact]
    public async Task A_listed_email_signs_in_as_its_account_without_a_key()
    {
        await using var s = await Start();

        var owner = await Me(s, Token("owner@example.test"));
        var alice = await Me(s, Token("alice@example.test"));

        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        Assert.Equal("owner", (await owner.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.OK, alice.StatusCode);
        Assert.Equal("alice", (await alice.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Any_token_that_is_not_valid_for_this_application_opens_nothing()
    {
        await using var s = await Start();
        using var stranger = RSA.Create(2048);

        string[] refused =
        [
            Token("bob@example.test"),
            Token("owner@example.test", aud: "another-app"),
            Token("owner@example.test", iss: "https://other.example.test"),
            Token("owner@example.test", expiresIn: TimeSpan.FromMinutes(-5)),
            Token("owner@example.test", key: stranger),
            Token("owner@example.test", kid: "unknown"),
            Token("owner@example.test")[..^4] + "AAAA",
            "not.a.token",
        ];
        foreach (var token in refused)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Me(s, token)).StatusCode);
    }

    [Fact]
    public async Task The_identity_probe_answers_204_not_401_when_there_is_none()
    {
        await using var s = await Start();
        using var http = new HttpClient { BaseAddress = s.BaseAddress };

        Assert.Equal(HttpStatusCode.NoContent, (await http.GetAsync(ChargehandServer.IdentityPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/v1/accounts/me")).StatusCode);
    }

    [Fact]
    public async Task Only_the_consoles_own_calls_take_the_identity()
    {
        await using var s = await Start();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Me(s, Token("owner@example.test"), console: false)).StatusCode);
    }

    [Fact]
    public async Task A_key_sent_with_the_token_decides_alone()
    {
        await using var s = await Start();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Me(s, Token("owner@example.test"), bearer: "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Me(s, null, bearer: TestServer.Key)).StatusCode);
    }

    [Fact]
    public async Task Keys_are_fetched_again_for_a_new_key_id_at_most_once_a_minute()
    {
        var fetches = 0;
        var rotated = RSA.Create(2048);
        await using var s = await Start(_ => Task.FromResult(++fetches == 1 ? Certs(Signer) : Certs(rotated, "k2")));

        Assert.Equal(HttpStatusCode.OK, (await Me(s, Token("owner@example.test"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Me(s, Token("owner@example.test", key: rotated, kid: "k2"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Me(s, Token("owner@example.test", key: rotated, kid: "k2"))).StatusCode);
        Assert.Equal(1, fetches);
    }

    [Fact]
    public void The_profile_refuses_an_identity_that_maps_to_no_account()
    {
        var bad = new CloudflareAccessSettings("https://team.example.test", "", new Dictionary<string, string> { ["carol"] = "carol" });

        var problems = bad.Problems([new AccountSettings("alice")]).ToList();

        Assert.Contains(problems, p => p.StartsWith("team_domain", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("audience", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'carol' is not an email", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("neither owner nor in http.accounts", StringComparison.Ordinal));
        Assert.Empty(Access.Problems([new AccountSettings("alice")]));
    }
}
