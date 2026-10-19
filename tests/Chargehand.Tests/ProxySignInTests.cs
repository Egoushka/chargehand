using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>Sign-in by the email a trusted single-sign-on proxy forwards (ADR 0071): the header counts only for a signed-on host, from the proxy's address, from the console.</summary>
public class ProxySignInTests
{
    private static ProxySignInSettings Sign(string host = "127.0.0.1") => new([host], ["proxy.internal"], new Dictionary<string, string>
    {
        ["Owner@Example.test"] = "owner",
        ["alice@example.test"] = "alice",
    });

    private static Task<TestServer> Start(string proxyAddress = "127.0.0.1", string host = "127.0.0.1") =>
        TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [new AccountSettings("alice", AllowServerCredential: true)],
            tweak: (_, s) => s with { ProxySignIn = Sign(host), ProxyResolver = (_, _) => Task.FromResult(new[] { IPAddress.Parse(proxyAddress) }) });

    private static async Task<HttpResponseMessage> Me(TestServer s, string? email, bool console = true, string? bearer = null)
    {
        using var http = new HttpClient { BaseAddress = s.BaseAddress };
        var get = new HttpRequestMessage(HttpMethod.Get, "/v1/accounts/me");
        if (email is not null)
            get.Headers.Add("Remote-Email", email);
        if (console)
            get.Headers.Add(ChargehandServer.ConsoleHeader, "1");
        if (bearer is not null)
            get.Headers.Authorization = new("Bearer", bearer);
        return await http.SendAsync(get);
    }

    [Fact]
    public async Task A_listed_email_from_the_proxy_signs_in_as_its_account()
    {
        await using var s = await Start();

        var owner = await Me(s, "owner@example.test");
        var alice = await Me(s, "ALICE@example.test");

        Assert.Equal("owner", (await owner.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
        Assert.Equal("alice", (await alice.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_header_proves_nothing_from_anyone_else_or_for_another_host()
    {
        await using var elsewhere = await Start(proxyAddress: "10.9.9.9");
        await using var otherHost = await Start(host: "console.example.test");

        Assert.Equal(HttpStatusCode.Unauthorized, (await Me(elsewhere, "owner@example.test")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Me(otherHost, "owner@example.test")).StatusCode);
    }

    [Fact]
    public async Task Only_the_consoles_calls_with_a_listed_email_and_no_key_take_it()
    {
        await using var s = await Start();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Me(s, "owner@example.test", console: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Me(s, "bob@example.test")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Me(s, "owner@example.test", bearer: "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Me(s, null)).StatusCode);
    }

    [Fact]
    public async Task Proxy_names_are_resolved_again_after_thirty_seconds()
    {
        var clock = new ManualTimeProvider();
        var address = IPAddress.Parse("10.1.1.1");
        var proxies = new TrustedProxies(["proxy.internal"], (_, _) => Task.FromResult(new[] { address }), clock);

        Assert.True(await proxies.IncludesAsync(address, default));
        var moved = IPAddress.Parse("10.2.2.2");
        address = moved;
        Assert.False(await proxies.IncludesAsync(moved, default));
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(await proxies.IncludesAsync(moved, default));
        Assert.False(await proxies.IncludesAsync(null, default));
    }

    [Fact]
    public void The_profile_refuses_a_setting_that_names_no_host_proxy_or_account()
    {
        var bad = new ProxySignInSettings(["https://console.example.test"], [], new Dictionary<string, string> { ["carol"] = "carol" });

        var problems = bad.Problems([new AccountSettings("alice")]).ToList();

        Assert.Contains(problems, p => p.StartsWith("hosts", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("proxies", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'carol' is not an email", StringComparison.Ordinal));
        Assert.Empty(Sign().Problems([new AccountSettings("alice")]));
    }
}
