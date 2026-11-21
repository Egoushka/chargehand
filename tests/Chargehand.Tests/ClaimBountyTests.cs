using Chargehand.Contracts;
using Chargehand.Evals;
using Chargehand.Modules.Research.Api;
using Chargehand.Verification;

namespace Chargehand.Tests;

/// <summary>docs/specs/2026-11-11-claim-bounty.md, issue #787: a queue of claims a "disprove" worker can earn a bounty point by
/// disproving with a resolved locator the support judge agrees contradicts the claim.</summary>
public class ClaimBountyTests
{
    private static (RepositoryRef Repo, EvidenceScope Scope) Setup(TempDir dir)
    {
        var repo = Runs.GitRepo(dir.Path);
        dir.Write(Path.Combine("repo", "a.txt"), "one\ntwo\nthree\nfour\n");
        repo = Runs.Commit(repo, "a.txt");
        return (repo, new EvidenceScope(repo.Path, repo.Commit, new HashSet<string>(), new HashSet<string>(), "", []));
    }

    private static ClaimRecord Claim(string id, string text, string commit, string? preset = null) =>
        new(id, text, ClaimSourceKind.Doc, "doc:1", commit, ClaimState.Open, DateTimeOffset.UtcNow, preset);

    private static string DisproveReply(string locator, string text = "the claim is false because the file says otherwise.") => $$"""
        ```json
        {"status":"completed","summary":"disproved","claims":[{"text":"{{text}}","evidence":["e1"],"confidence":0.9}],
         "evidence":[{"id":"e1","kind":"file","locator":"{{locator}}"}],"artifacts":[],"open_questions":[],"confidence":0.9}
        ```
        """;

    private static string Verdict(string verdict) => $$"""{"verdicts":[{"claim":1,"verdict":"{{verdict}}","reason":"r"}]}""";

    [Fact]
    public async Task Queue_hands_out_one_open_claim_and_expires_the_lease()
    {
        using var dir = new TempDir();
        var clock = new ManualTimeProvider();
        var queue = new ClaimQueue(Path.Combine(dir.Path, "claims.jsonl"), clock);
        await queue.SeedAsync([Claim("c1", "x", "deadbeef")], default);

        var leased = await queue.LeaseAsync(TimeSpan.FromMinutes(10), default);
        Assert.Equal("c1", leased!.Id);
        Assert.Equal(ClaimState.Leased, leased.State);

        Assert.Null(await queue.LeaseAsync(TimeSpan.FromMinutes(10), default));

        clock.Advance(TimeSpan.FromMinutes(11));
        var relet = await queue.LeaseAsync(TimeSpan.FromMinutes(10), default);
        Assert.Equal("c1", relet!.Id);
        Assert.Equal(ClaimState.Leased, relet.State);
    }

    [Fact]
    public async Task Disproof_with_unresolved_locator_earns_nothing()
    {
        using var dir = new TempDir();
        var (repo, scope) = Setup(dir);
        var queue = new ClaimQueue(Path.Combine(dir.Path, "claims.jsonl"));
        var ledger = new BountyLedger(Path.Combine(dir.Path, "bounty.jsonl"));
        var id = ClaimRecord.IdOf(ClaimSourceKind.Doc, "doc:1", "two is one");
        await queue.SeedAsync([Claim(id, "two is one", repo.Commit)], default);
        var runtime = new ScriptedRuntime("", Verdict("supported"));

        var outcome = await ClaimBounty.RunOnceAsync(queue, ledger, runtime, null,
            (_, _) => Task.FromResult(DisproveReply("nope.txt:1")), scope, "disprove", TimeSpan.FromMinutes(10), default);

        Assert.Equal(DisproofOutcome.Open, outcome);
        Assert.Equal(ClaimState.Open, (await queue.FindAsync(id, default))!.State);
        Assert.Empty(await ledger.ReadAsync(default));
        // The unresolved locator never reaches the support judge.
        Assert.Empty(runtime.IntakePrompts);
    }

    [Fact]
    public async Task Disproving_your_own_claim_earns_nothing()
    {
        using var dir = new TempDir();
        var (repo, scope) = Setup(dir);
        var queue = new ClaimQueue(Path.Combine(dir.Path, "claims.jsonl"));
        var ledger = new BountyLedger(Path.Combine(dir.Path, "bounty.jsonl"));
        var id = ClaimRecord.IdOf(ClaimSourceKind.Result, "run-1", "two is one");
        await queue.SeedAsync([new ClaimRecord(id, "two is one", ClaimSourceKind.Result, "run-1", repo.Commit, ClaimState.Open, DateTimeOffset.UtcNow, "disprove")], default);
        var runtime = new ScriptedRuntime("", Verdict("supported"));

        var outcome = await ClaimBounty.RunOnceAsync(queue, ledger, runtime, null,
            (_, _) => Task.FromResult(DisproveReply("a.txt:1")), scope, "disprove", TimeSpan.FromMinutes(10), default);

        Assert.Equal(DisproofOutcome.Open, outcome);
        Assert.Empty(await ledger.ReadAsync(default));
        // Refused before the evidence is even scored.
        Assert.Empty(runtime.IntakePrompts);
    }

    [Fact]
    public async Task Accepted_disproof_appends_one_bounty_line_for_its_preset()
    {
        using var dir = new TempDir();
        var (repo, scope) = Setup(dir);
        var queue = new ClaimQueue(Path.Combine(dir.Path, "claims.jsonl"));
        var ledger = new BountyLedger(Path.Combine(dir.Path, "bounty.jsonl"));
        var id = ClaimRecord.IdOf(ClaimSourceKind.Doc, "doc:1", "two is one");
        await queue.SeedAsync([Claim(id, "two is one", repo.Commit)], default);
        var runtime = new ScriptedRuntime("", Verdict("supported"));

        var outcome = await ClaimBounty.RunOnceAsync(queue, ledger, runtime, null,
            (_, _) => Task.FromResult(DisproveReply("a.txt:2")), scope, "disprove", TimeSpan.FromMinutes(10), default);

        Assert.Equal(DisproofOutcome.Disproved, outcome);
        Assert.Equal(ClaimState.Disproved, (await queue.FindAsync(id, default))!.State);
        var entry = Assert.Single(await ledger.ReadAsync(default));
        Assert.Equal(id, entry.ClaimId);
        Assert.Equal("disprove", entry.Preset);
        Assert.Equal(1, entry.Points);
    }

    [Fact]
    public async Task Seeded_false_claim_is_disproved_and_the_bounty_recorded()
    {
        using var dir = new TempDir();
        var seeded = ClaimSeeds.Read(File.ReadLines(Repo.Path("evals", "claim-bounty-seed.jsonl")));
        var claim = Assert.Single(seeded);
        var scope = new EvidenceScope(Repo.Root, claim.Commit, new HashSet<string>(), new HashSet<string>(), "", []);
        var queue = new ClaimQueue(Path.Combine(dir.Path, "claims.jsonl"));
        var ledger = new BountyLedger(Path.Combine(dir.Path, "bounty.jsonl"));
        await queue.SeedAsync(seeded, default);
        var runtime = new ScriptedRuntime("", Verdict("supported"));

        var outcome = await ClaimBounty.RunOnceAsync(queue, ledger, runtime, null,
            (_, _) => Task.FromResult(DisproveReply("CLAUDE.md:3", "the claim is false because CLAUDE.md says chargehand is a .NET orchestrator.")),
            scope, "disprove", TimeSpan.FromMinutes(10), default);

        Assert.Equal(DisproofOutcome.Disproved, outcome);
        Assert.Equal(ClaimState.Disproved, (await queue.FindAsync(claim.Id, default))!.State);
        var entry = Assert.Single(await ledger.ReadAsync(default));
        Assert.Equal(claim.Id, entry.ClaimId);
        Assert.Equal("disprove", entry.Preset);
    }
}
