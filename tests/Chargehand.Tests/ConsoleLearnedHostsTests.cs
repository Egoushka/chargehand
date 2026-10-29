using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Gateway.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0078, decision 4: the owner reviews the hosts each repository's sessions were refused and approves one for that repository.</summary>
public class ConsoleLearnedHostsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    private static Task<HttpResponseMessage> Post(HttpClient http, string action, string repository, string host) =>
        http.PostAsJsonAsync($"/v1/egress/learned/{action}", new { repository, host });

    [Fact]
    public async Task The_owner_lists_the_refused_hosts_per_repository_and_approves_and_revokes_one()
    {
        await using var rig = await ConsoleRig.StartAsync();
        rig.Driven.LearnedHosts!.Record("o/r", "run-1", [new EgressRefusal("static.rust-lang.org", At), new EgressRefusal("static.rust-lang.org", At.AddMinutes(1))]);

        var repo = Assert.Single((await ConsoleRig.Get(rig.Owner, "/v1/egress/learned")).GetProperty("repositories").EnumerateArray());
        Assert.Equal("o/r", repo.GetProperty("repository").GetString());
        var host = Assert.Single(repo.GetProperty("hosts").EnumerateArray());
        Assert.Equal(("static.rust-lang.org", 2, 1, "run-1"),
            (host.GetProperty("host").GetString(), host.GetProperty("refusals").GetInt32(), host.GetProperty("batches").GetInt32(), host.GetProperty("last_batch").GetString()));
        Assert.False(host.TryGetProperty("approved", out _)); // null fields are left out

        var approved = await Post(rig.Owner, "approve", "o/r", "static.rust-lang.org");
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.True((await approved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("changed").GetBoolean());
        Assert.False((await (await Post(rig.Owner, "approve", "o/r", "static.rust-lang.org")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("changed").GetBoolean());
        Assert.Equal(["static.rust-lang.org"], rig.Driven.LearnedHosts.ApprovedFor("o/r"));

        Assert.Equal(HttpStatusCode.OK, (await Post(rig.Owner, "revoke", "o/r", "static.rust-lang.org")).StatusCode);
        Assert.Empty(rig.Driven.LearnedHosts.ApprovedFor("o/r"));
    }

    [Fact]
    public async Task A_host_never_refused_is_not_found_a_bad_body_is_refused_and_an_account_cannot_approve()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [new AccountSettings("alice", Driven: true, AllowServerCredential: true)]);
        rig.Driven.LearnedHosts!.Record("o/r", "run-1", [new EgressRefusal("static.rust-lang.org", At)]);

        Assert.Equal(HttpStatusCode.NotFound, (await Post(rig.Owner, "approve", "o/r", "evil.example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(rig.Owner, "approve", "o/other", "static.rust-lang.org")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Owner.PostAsJsonAsync("/v1/egress/learned/approve", new { repository = "o/r" })).StatusCode);
        using var alice = rig.As("alice");
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(alice, "approve", "o/r", "static.rust-lang.org")).StatusCode);
        Assert.Empty(rig.Driven.LearnedHosts.ApprovedFor("o/r"));
    }
}
