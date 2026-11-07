using System.Net;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary><c>GET /v1/fleet</c> (fleet/v1): runs, claims and open pull requests in one read, each with its state, age and blocker.</summary>
public class ConsoleFleetTests
{
    private static readonly string[] RunStates = ["queued", "running"];

    private static AccountSettings Alice() => new("alice", Driven: true, AllowServerCredential: true);

    private static string[] Names(JsonElement item) => [.. item.EnumerateObject().Select(p => p.Name)];

    private static Profile Roots(string root) => Runs.Profile(root) with
    {
        RepositoryRoots = [Path.Combine(root, "roots")],
        Driven = new DrivenSettings(Integrator: new DrivenIntegrator(Repositories: new Dictionary<string, string> { ["o/r"] = "ask" })),
    };

    private static void Claim(string repo, int issue, string content, bool asCommit)
    {
        string target;
        if (asCommit)
            target = Runs.Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit-tree", "HEAD^{tree}", "-m", content).Trim();
        else
        {
            var file = Path.Combine(repo, $".claim-{issue}");
            File.WriteAllText(file, content);
            target = Runs.Git(repo, "hash-object", "-w", file).Trim();
            File.Delete(file);
        }
        Runs.Git(repo, "update-ref", $"refs/claims/{issue}", target);
    }

    [Fact]
    public async Task The_fleet_pins_runs_claims_and_pull_requests_each_with_state_age_and_blocked_by()
    {
        await using var rig = await ConsoleRig.StartAsync(profile: Roots, hold: true);
        var repo = Runs.GitRepo(Path.Combine(rig.Root, "roots"));
        Runs.SetOrigin(repo, "https://github.com/o/r.git");
        Claim(repo.Path, 123, "holder: session-1\nexpires: 2026-10-15T14:00:00Z", asCommit: true);
        Claim(repo.Path, 124, """{"holder":"run-9","expires_at":"2026-10-15T10:00:00Z"}""", asCommit: false);
        Claim(repo.Path, 125, "no holder here", asCommit: false);
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b1", "t1", actor: "alice", at: ConsoleRig.Now.AddHours(-3), pr: ConsoleSeed.Pull(7)));
        Assert.Equal(HttpStatusCode.Accepted, (await rig.Server.PostAsync(Runs.DraftRequest(), 0)).StatusCode);

        var body = await ConsoleRig.Get(rig.Owner, "/v1/fleet");

        Assert.Equal(["contract_version", "generated_at", "runs", "claims", "pull_requests", "not_recorded"], Names(body));
        Assert.Equal("fleet/v1", body.GetProperty("contract_version").GetString());
        var run = Assert.Single(body.GetProperty("runs").EnumerateArray());
        Assert.Equal(["id", "kind", "actor", "state", "since", "age_seconds", "blocked_by"], Names(run));
        Assert.Equal("run", run.GetProperty("kind").GetString());
        Assert.Contains(run.GetProperty("state").GetString(), RunStates);
        Assert.Equal(run.GetProperty("state").GetString() == "queued" ? "waiting_for_slot" : null, run.GetProperty("blocked_by").GetString());
        var claims = body.GetProperty("claims").EnumerateArray().ToList();
        Assert.Equal([123, 124], claims.Select(k => k.GetProperty("issue").GetInt32()));
        Assert.Equal(["repository", "issue", "holder", "state", "expires_at", "since", "age_seconds", "blocked_by"], Names(claims[0]));
        Assert.Equal(("repo", "session-1", "held", JsonValueKind.Null), (claims[0].GetProperty("repository").GetString(), claims[0].GetProperty("holder").GetString(), claims[0].GetProperty("state").GetString(), claims[0].GetProperty("blocked_by").ValueKind));
        Assert.Equal(("run-9", "expired"), (claims[1].GetProperty("holder").GetString(), claims[1].GetProperty("state").GetString()));
        var pull = Assert.Single(body.GetProperty("pull_requests").EnumerateArray());
        Assert.Equal(["url", "number", "repository", "actor", "state", "since", "age_seconds", "blocked_by"], Names(pull));
        Assert.Equal((7, "o/r", "awaiting_person", "policy_ask", 3 * 3600), (pull.GetProperty("number").GetInt32(), pull.GetProperty("repository").GetString(), pull.GetProperty("state").GetString(),
            pull.GetProperty("blocked_by").GetString(), pull.GetProperty("age_seconds").GetInt32()));
    }

    [Fact]
    public async Task A_checkout_with_no_claim_refs_gives_an_empty_claims_list_and_a_pull_request_nothing_blocks_has_a_null_blocker()
    {
        await using var rig = await ConsoleRig.StartAsync(profile: root => Roots(root) with { Driven = new DrivenSettings(Integrator: new DrivenIntegrator(Repositories: new Dictionary<string, string> { ["o/r"] = "merge" })) });
        Runs.SetOrigin(Runs.GitRepo(Path.Combine(rig.Root, "roots")), "https://github.com/o/r.git");
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b1", "t1", pr: ConsoleSeed.Pull(7)));

        var body = await ConsoleRig.Get(rig.Owner, "/v1/fleet");

        Assert.Equal(0, body.GetProperty("claims").GetArrayLength());
        Assert.Equal(0, body.GetProperty("runs").GetArrayLength());
        var pull = Assert.Single(body.GetProperty("pull_requests").EnumerateArray());
        Assert.Equal(("open", JsonValueKind.Null), (pull.GetProperty("state").GetString(), pull.GetProperty("blocked_by").ValueKind));
    }

    [Fact]
    public async Task An_account_sees_its_own_runs_and_pull_requests_and_the_claims_of_the_checkouts_it_may_use()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()], profile: Roots, hold: true);
        var repo = Runs.GitRepo(Path.Combine(rig.Root, "roots"));
        Claim(repo.Path, 5, "holder: s\nexpires: 2026-10-15T14:00:00Z", asCommit: true);
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b1", "t1", actor: "alice", pr: ConsoleSeed.Pull(7)));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b2", "t1", pr: ConsoleSeed.Pull(8)));
        Assert.Equal(HttpStatusCode.Accepted, (await rig.Server.PostAsync(Runs.DraftRequest(), 0)).StatusCode);
        using var alice = rig.As("alice");

        var body = await ConsoleRig.Get(alice, "/v1/fleet");

        Assert.Equal(0, body.GetProperty("runs").GetArrayLength());
        Assert.Equal([7], body.GetProperty("pull_requests").EnumerateArray().Select(p => p.GetProperty("number").GetInt32()));
        Assert.Equal([5], body.GetProperty("claims").EnumerateArray().Select(k => k.GetProperty("issue").GetInt32()));
    }
}
