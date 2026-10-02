using Chargehand.Contracts;
using Chargehand.Evals;
using Chargehand.Verification;

namespace Chargehand.Tests;

/// <summary>Goal 0.9 (spec 2026-09-30-public-benchmark-design, decision 6): one scorer gives every arm's claims the same path to a verdict.</summary>
public class ClaimScorerTests
{
    private const string Secret = "arm-plain-secret";

    private static string Verdicts(params string[] verdicts) =>
        "{\"verdicts\":[" + string.Join(",", verdicts.Select((v, i) => $"{{\"claim\":{i + 1},\"verdict\":\"{v}\",\"reason\":\"r\"}}")) + "]}";

    private static (RepositoryRef Repo, EvidenceScope Scope) Setup(TempDir dir)
    {
        var repo = Runs.GitRepo(dir.Path);
        dir.Write(Path.Combine("repo", "a.txt"), "one\ntwo\nthree\nfour\n");
        repo = Runs.Commit(repo, "a.txt");
        return (repo, new EvidenceScope(repo.Path, repo.Commit, new HashSet<string>(), new HashSet<string>(), "", []));
    }

    private static BenchClaim Claim(string text, int run = 1, string question = "q01", string arm = Secret, params string[] locators) =>
        new(arm, question, run, text, locators);

    [Fact]
    public async Task A_claim_with_no_locators_is_uncited_and_never_reaches_the_judge()
    {
        using var dir = new TempDir();
        var (_, scope) = Setup(dir);
        var runtime = new ScriptedRuntime("", "no judge reply");
        var scored = await ClaimScorer.ScoreAsync(runtime, null, [Claim("It is somewhere.")], scope, default);
        Assert.Equal(ClaimOutcome.Uncited, Assert.Single(scored).Outcome);
        Assert.Empty(runtime.IntakePrompts);
    }

    [Fact]
    public async Task A_locator_to_a_missing_path_or_past_the_end_is_unresolved()
    {
        using var dir = new TempDir();
        var (_, scope) = Setup(dir);
        var runtime = new ScriptedRuntime("", "no judge reply");
        var scored = await ClaimScorer.ScoreAsync(runtime, null, [Claim("Missing.", locators: "nope.txt:1"), Claim("Past the end.", locators: "a.txt:40-41"), Claim("Not a locator.", locators: "a.txt")], scope, default);
        Assert.All(scored, s => Assert.Equal(ClaimOutcome.Unresolved, s.Outcome));
        Assert.Empty(runtime.IntakePrompts);
    }

    [Fact]
    public async Task One_unresolved_locator_makes_the_claim_unresolved_even_when_another_resolves()
    {
        using var dir = new TempDir();
        var (_, scope) = Setup(dir);
        var scored = await ClaimScorer.ScoreAsync(new ScriptedRuntime("", "x"), null, [Claim("Half.", locators: ["a.txt:1", "nope.txt:1"])], scope, default);
        Assert.Equal(ClaimOutcome.Unresolved, Assert.Single(scored).Outcome);
    }

    [Fact]
    public async Task The_judges_three_verdicts_map_to_three_outcomes()
    {
        using var dir = new TempDir();
        var (_, scope) = Setup(dir);
        var claims = new[] { Claim("a", locators: "a.txt:1"), Claim("b", locators: "a.txt:2"), Claim("c", locators: "a.txt:3") };
        var scored = await ClaimScorer.ScoreAsync(new ScriptedRuntime("", Verdicts("supported", "partial", "unsupported")), null, claims, scope, default);
        Assert.Equal([ClaimOutcome.Supported, ClaimOutcome.Partial, ClaimOutcome.Unsupported], scored.Select(s => s.Outcome));
    }

    [Fact]
    public async Task A_judge_that_fails_twice_leaves_the_claim_unchecked_and_the_run_completes()
    {
        using var dir = new TempDir();
        var (_, scope) = Setup(dir);
        var claims = new[] { Claim("a", run: 1, locators: "a.txt:1"), Claim("b", run: 2, locators: "a.txt:2") };
        var runtime = new ScriptedRuntime("", "not json", "not json", Verdicts("supported"));
        var scored = await ClaimScorer.ScoreAsync(runtime, null, claims, scope, default);
        Assert.Equal([ClaimOutcome.Unchecked, ClaimOutcome.Supported], scored.Select(s => s.Outcome));
    }

    [Fact]
    public async Task Claims_are_judged_in_batches_per_arm_question_and_run_never_beside_another_runs()
    {
        using var dir = new TempDir();
        var (_, scope) = Setup(dir);
        var claims = new[]
        {
            Claim("r1 first", run: 1, locators: "a.txt:1"),
            Claim("r2 only", run: 2, locators: "a.txt:2"),
            Claim("r1 second", run: 1, locators: "a.txt:3"),
            Claim("q2 only", run: 1, question: "q02", locators: "a.txt:4"),
        };
        var runtime = new ScriptedRuntime("", Verdicts("supported", "partial"), Verdicts("unsupported"), Verdicts("supported"));
        var scored = await ClaimScorer.ScoreAsync(runtime, null, claims, scope, default);
        var prompts = runtime.IntakePrompts.ToArray();
        Assert.Equal(3, prompts.Length);
        Assert.Contains("r1 first", prompts[0], StringComparison.Ordinal);
        Assert.Contains("r1 second", prompts[0], StringComparison.Ordinal);
        Assert.DoesNotContain("r2 only", prompts[0], StringComparison.Ordinal);
        Assert.DoesNotContain("q2 only", prompts[0], StringComparison.Ordinal);
        // Output keeps the input order, not the batch order.
        Assert.Equal(["r1 first", "r2 only", "r1 second", "q2 only"], scored.Select(s => s.Claim.Text));
        Assert.Equal([ClaimOutcome.Supported, ClaimOutcome.Unsupported, ClaimOutcome.Partial, ClaimOutcome.Supported], scored.Select(s => s.Outcome));
    }

    [Fact]
    public async Task The_arm_never_appears_in_the_judges_prompt()
    {
        using var dir = new TempDir();
        var (_, scope) = Setup(dir);
        var runtime = new ScriptedRuntime("", Verdicts("supported"));
        await ClaimScorer.ScoreAsync(runtime, null, [Claim("a", locators: "a.txt:1")], scope, default);
        Assert.DoesNotContain(Secret, Assert.Single(runtime.IntakePrompts), StringComparison.Ordinal);
    }

    [Fact]
    public void Claims_and_verdicts_round_trip_through_jsonl_one_line_per_claim()
    {
        var claims = new[] { Claim("a", locators: "a.txt:1"), Claim("b") };
        var lines = ClaimScorer.ToJsonl(claims.Select(c => new ScoredClaim(c, ClaimOutcome.Supported, "r"))).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains("\"outcome\":\"supported\"", lines[0], StringComparison.Ordinal);
        var read = ClaimScorer.ReadClaims(claims.Select(ClaimScorer.ClaimLine));
        Assert.Equal(["a", "b"], read.Select(c => c.Text));
        Assert.Equal(["a.txt:1"], read[0].Locators);
        Assert.Empty(read[1].Locators);
    }
}
