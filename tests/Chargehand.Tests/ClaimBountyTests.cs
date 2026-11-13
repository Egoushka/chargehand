using Chargehand.Contracts;
using Chargehand.Evals;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Verification;

namespace Chargehand.Tests;

/// <summary>Spec 2026-11-11-claim-bounty: a queue of claims for workers to disprove, and a point only for a disproof whose cited text contradicts the claim.</summary>
public class ClaimBountyTests
{
    private const string Preset = "disprove";
    private const string Commit = "0000000000000000000000000000000000000000";

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string Verdict(string verdict) => $"{{\"verdicts\":[{{\"claim\":1,\"verdict\":\"{verdict}\",\"reason\":\"r\"}}]}}";

    private static ResultContract Disproof(string text, params string[] locators) =>
        new("result/v1", "t", "n", new string('0', 32), new PromptChain([], new AsSent("", "", "", "")), ResultStatus.Completed, "",
            [new Claim(text, [.. locators.Select((_, i) => $"e{i}")], 1)],
            [.. locators.Select((l, i) => new Evidence($"e{i}", EvidenceKind.File, l))], [], [], 1, new Usage(0, 0, 0, 0, 0));

    private static ResultContract Stands() =>
        new("result/v1", "t", "n", new string('0', 32), new PromptChain([], new AsSent("", "", "", "")), ResultStatus.Completed, "", [], [], [], ["stands"], 1,
            new Usage(0, 0, 0, 0, 0));

    private static (string Repo, string Commit, ClaimQueue Queue, BountyLedger Ledger) Setup(TempDir dir)
    {
        var repo = Runs.GitRepo(dir.Path);
        dir.Write(Path.Combine("repo", "a.txt"), "the sky is green\nthe grass is red\n");
        repo = Runs.Commit(repo, "a.txt");
        return (repo.Path, repo.Commit, new ClaimQueue(Path.Combine(dir.Path, "claims.jsonl")), new BountyLedger(Path.Combine(dir.Path, "bounty.jsonl")));
    }

    [Fact]
    public async Task Queue_hands_out_one_open_claim_and_expires_the_lease()
    {
        using var dir = new TempDir();
        var clock = new Clock(DateTimeOffset.Parse("2026-11-11T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var queue = new ClaimQueue(Path.Combine(dir.Path, "claims.jsonl"), clock);
        await queue.AddAsync("one", "result:r1", Commit, "prior-art", default);
        await queue.AddAsync("two", "result:r2", Commit, "prior-art", default);
        await queue.AddAsync("one", "result:r1", Commit, "prior-art", default);
        Assert.Equal(2, (await queue.ReadAsync(default)).Count);

        var first = await queue.LeaseNextAsync(TimeSpan.FromMinutes(10), default);
        Assert.Equal("one", first?.Text);
        Assert.Null(await queue.LeaseNextAsync(TimeSpan.FromMinutes(10), default));

        clock.Now += TimeSpan.FromMinutes(11);
        Assert.Equal("one", (await queue.LeaseNextAsync(TimeSpan.FromMinutes(10), default))?.Text);

        await queue.SetStateAsync(first!.Id, ClaimState.Stands, default);
        clock.Now += TimeSpan.FromMinutes(11);
        Assert.Equal("two", (await queue.LeaseNextAsync(TimeSpan.FromMinutes(10), default))?.Text);
    }

    [Fact]
    public async Task A_claim_whose_commit_is_not_an_ancestor_becomes_stale()
    {
        using var dir = new TempDir();
        var queue = new ClaimQueue(Path.Combine(dir.Path, "claims.jsonl"));
        await queue.AddAsync("kept", "result:r1", "good", null, default);
        await queue.AddAsync("gone", "result:r2", "bad", null, default);
        Assert.Equal(1, await queue.MarkStaleAsync(c => Task.FromResult(c == "good"), default));
        var claims = await queue.ReadAsync(default);
        Assert.Equal(ClaimState.Stale, claims.Single(c => c.Text == "gone").State);
        Assert.Equal(ClaimState.Open, claims.Single(c => c.Text == "kept").State);
    }

    [Fact]
    public async Task Disproof_with_unresolved_locator_earns_nothing()
    {
        using var dir = new TempDir();
        var (repo, commit, queue, ledger) = Setup(dir);
        var claim = await queue.AddAsync("the sky is blue", "result:r1", commit, "prior-art", default);
        var runtime = new ScriptedRuntime("", ScriptedRuntime.Spec(), Verdict("supported"));
        var outcome = await ClaimBounty.JudgeAsync(runtime, null, queue, ledger, claim, Disproof("the sky is blue is false", "nope.txt:1"), "run1", Preset, repo, default);
        Assert.Equal(DisproofOutcome.Open, outcome);
        Assert.Equal(ClaimState.Open, (await queue.ReadAsync(default)).Single().State);
        Assert.Empty(await ledger.ReadAsync(default));
    }

    [Fact]
    public async Task Disproving_your_own_claim_earns_nothing()
    {
        using var dir = new TempDir();
        var (repo, commit, queue, ledger) = Setup(dir);
        var claim = await queue.AddAsync("the sky is blue", "result:r1", commit, Preset, default);
        var runtime = new ScriptedRuntime("", ScriptedRuntime.Spec(), Verdict("supported"));
        var outcome = await ClaimBounty.JudgeAsync(runtime, null, queue, ledger, claim, Disproof("the sky is blue is false", "a.txt:1"), "run1", Preset, repo, default);
        Assert.Equal(DisproofOutcome.SelfDisproof, outcome);
        Assert.Empty(await ledger.ReadAsync(default));
        Assert.Equal(ClaimState.Open, (await queue.ReadAsync(default)).Single().State);
    }

    [Fact]
    public async Task Accepted_disproof_appends_one_bounty_line_for_its_preset()
    {
        using var dir = new TempDir();
        var (repo, commit, queue, ledger) = Setup(dir);
        var claim = await queue.AddAsync("the sky is blue", "result:r1", commit, "prior-art", default);
        var runtime = new ScriptedRuntime("", ScriptedRuntime.Spec(), Verdict("supported"));
        var disproof = Disproof("the sky is blue is false because a.txt says green", "a.txt:1");
        Assert.Equal(DisproofOutcome.Disproved, await ClaimBounty.JudgeAsync(runtime, null, queue, ledger, claim, disproof, "run1", Preset, repo, default));
        Assert.Equal(ClaimState.Disproved, (await queue.ReadAsync(default)).Single().State);
        var line = Assert.Single(await ledger.ReadAsync(default));
        Assert.Equal((claim.Id, "run1", Preset, 1), (line.ClaimId, line.RunId, line.Preset, line.Points));

        await ClaimBounty.JudgeAsync(runtime, null, queue, ledger, claim, disproof, "run2", Preset, repo, default);
        Assert.Single(await ledger.ReadAsync(default));
    }

    [Fact]
    public async Task A_disproof_the_judge_does_not_support_earns_nothing_and_stands_is_recorded_without_a_point()
    {
        using var dir = new TempDir();
        var (repo, commit, queue, ledger) = Setup(dir);
        var claim = await queue.AddAsync("the sky is green", "result:r1", commit, "prior-art", default);
        var runtime = new ScriptedRuntime("", ScriptedRuntime.Spec(), Verdict("unsupported"));
        Assert.Equal(DisproofOutcome.Open, await ClaimBounty.JudgeAsync(runtime, null, queue, ledger, claim, Disproof("it is false", "a.txt:1"), "run1", Preset, repo, default));
        Assert.Equal(DisproofOutcome.Stands, await ClaimBounty.JudgeAsync(runtime, null, queue, ledger, claim, Stands(), "run2", Preset, repo, default));
        Assert.Equal(ClaimState.Stands, (await queue.ReadAsync(default)).Single().State);
        Assert.Empty(await ledger.ReadAsync(default));
    }

    [Fact]
    public async Task Seeded_false_claim_is_disproved_and_the_bounty_recorded()
    {
        using var dir = new TempDir();
        var seed = ClaimBounty.ReadSeed(File.ReadAllLines(Repo.Path("evals", "claim-bounty-seed.jsonl")));
        var repo = Runs.GitRepo(dir.Path);
        File.Copy(Repo.Path("CLAUDE.md"), Path.Combine(repo.Path, "CLAUDE.md"));
        repo = Runs.Commit(repo, "CLAUDE.md");
        var reply = "```json\n{\"status\":\"completed\",\"summary\":\"false\"," +
            "\"claims\":[{\"text\":\"The claim is false because CLAUDE.md describes a .NET orchestrator.\",\"evidence\":[\"e1\"],\"confidence\":0.9}]," +
            "\"evidence\":[{\"id\":\"e1\",\"kind\":\"file\",\"locator\":\"CLAUDE.md:3\"}],\"artifacts\":[],\"open_questions\":[],\"confidence\":0.9}\n```";
        var runtime = new ScriptedRuntime(reply, ScriptedRuntime.Spec(), Verdict("supported"));
        var queue = new ClaimQueue(Path.Combine(dir.Path, "claims.jsonl"));
        var ledger = new BountyLedger(Path.Combine(dir.Path, "bounty.jsonl"));
        var orchestrator = Runs.Orchestrator(runtime, dir.Path, new JsonlRunLog(Path.Combine(dir.Path, "runs.jsonl")));
        var counts = await ClaimBounty.RunAsync(orchestrator, runtime, null, queue, ledger, seed, repo.Path, repo.Commit, default);
        Assert.Equal(1, counts.Disproved);
        var claim = Assert.Single(await queue.ReadAsync(default));
        Assert.Equal(ClaimState.Disproved, claim.State);
        var line = Assert.Single(File.ReadAllLines(Path.Combine(dir.Path, "bounty.jsonl")));
        Assert.Contains(claim.Id, line, StringComparison.Ordinal);
        Assert.Contains("\"preset\":\"disprove\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unresolved_claim_is_released_and_the_next_claim_is_still_processed()
    {
        using var dir = new TempDir();
        var repo = Runs.GitRepo(dir.Path);
        File.Copy(Repo.Path("CLAUDE.md"), Path.Combine(repo.Path, "CLAUDE.md"));
        repo = Runs.Commit(repo, "CLAUDE.md");
        var reply = "```json\n{\"status\":\"completed\",\"summary\":\"false\"," +
            "\"claims\":[{\"text\":\"It is false because CLAUDE.md says so.\",\"evidence\":[\"e1\"],\"confidence\":0.9}]," +
            "\"evidence\":[{\"id\":\"e1\",\"kind\":\"file\",\"locator\":\"CLAUDE.md:3\"}],\"artifacts\":[],\"open_questions\":[],\"confidence\":0.9}\n```";
        var runtime = new ScriptedRuntime(reply, ScriptedRuntime.Spec(), Verdict("unsupported"), ScriptedRuntime.Spec(), Verdict("supported"));
        var queue = new ClaimQueue(Path.Combine(dir.Path, "claims.jsonl"));
        var ledger = new BountyLedger(Path.Combine(dir.Path, "bounty.jsonl"));
        var orchestrator = Runs.Orchestrator(runtime, dir.Path, new JsonlRunLog(Path.Combine(dir.Path, "runs.jsonl")));
        var seed = new[] { new SeedClaim("first claim", "result:seed1"), new SeedClaim("second claim", "result:seed2") };

        var counts = await ClaimBounty.RunAsync(orchestrator, runtime, null, queue, ledger, seed, repo.Path, repo.Commit, default);

        Assert.Equal((1, 1), (counts.Disproved, counts.Open));
        var claims = await queue.ReadAsync(default);
        var first = claims.Single(c => c.Text == "first claim");
        Assert.Equal((ClaimState.Open, null), (first.State, first.LeaseUntil));
        Assert.Equal(ClaimState.Disproved, claims.Single(c => c.Text == "second claim").State);

        var again = await ClaimBounty.RunAsync(orchestrator, runtime, null, queue, ledger, seed, repo.Path, repo.Commit, default);
        Assert.Equal(1, again.Skipped);
    }

    [Fact]
    public async Task The_ledger_refuses_a_second_positive_line_for_one_claim()
    {
        using var dir = new TempDir();
        var ledger = new BountyLedger(Path.Combine(dir.Path, "bounty.jsonl"));
        Assert.NotNull(await ledger.AppendAsync("c1", "run1", Preset, 1, default));
        Assert.Null(await ledger.AppendAsync("c1", "run2", Preset, 1, default));
        Assert.NotNull(await ledger.AppendAsync("c1", "run3", Preset, -1, default));
        Assert.NotNull(await ledger.AppendAsync("c2", "run4", Preset, 1, default));
        Assert.Equal(3, (await ledger.ReadAsync(default)).Count);
    }

    [Fact]
    public async Task Judging_one_claim_twice_at_once_awards_one_point()
    {
        using var dir = new TempDir();
        var (repo, commit, queue, ledger) = Setup(dir);
        var claim = await queue.AddAsync("the sky is blue", "result:r1", commit, "prior-art", default);
        var runtime = new ScriptedRuntime("", ScriptedRuntime.Spec(), Verdict("supported"));
        var disproof = Disproof("the sky is blue is false because a.txt says green", "a.txt:1");
        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => ClaimBounty.JudgeAsync(runtime, null, queue, ledger, claim, disproof, $"run{i}", Preset, repo, default)));
        Assert.Single(await ledger.ReadAsync(default));
    }

    [Fact]
    public async Task Ancestry_tells_not_an_ancestor_from_a_git_failure()
    {
        using var dir = new TempDir();
        var first = Runs.GitRepo(dir.Path);
        dir.Write(Path.Combine("repo", "README.md"), "changed\n");
        var second = Runs.Commit(first, "README.md");
        Assert.True(await ClaimQueue.IsAncestorAsync(first.Path, first.Commit, second.Commit, default));
        Assert.False(await ClaimQueue.IsAncestorAsync(first.Path, second.Commit, first.Commit, default));
        Assert.Null(await ClaimQueue.IsAncestorAsync(first.Path, Commit, second.Commit, default));
        Assert.Null(await ClaimQueue.IsAncestorAsync(Path.Combine(dir.Path, "no-such-repo"), first.Commit, second.Commit, default));
    }
}
