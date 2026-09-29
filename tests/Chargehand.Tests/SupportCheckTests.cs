using Chargehand.Contracts;
using Chargehand.Memory;
using Chargehand.RunLog;
using Chargehand.Verification;

namespace Chargehand.Tests;

/// <summary>Goal 0.8 (ADR 0036): what the support check does to a result, and that runs apply it.</summary>
public class SupportCheckTests
{
    private static ResultContract Contract(params Claim[] claims) =>
        new("result/v1", "r", "n", new string('0', 32), new PromptChain([], new AsSent("2.0.16", "build", "p/m", "2026-09-30")), ResultStatus.Completed, "The summary.", claims,
            [new Evidence("e1", EvidenceKind.File, "a.cs:1"), new Evidence("e2", EvidenceKind.File, "b.cs:1"), new Evidence("e3", EvidenceKind.File, "c.cs:1")], [], ["Earlier question."], 0.8,
            new Usage(0, 0, 0, 0, 0));

    private static Claim C(string text, params string[] evidence) => new(text, evidence, 0.8);

    [Fact]
    public void Each_verdict_has_its_effect_and_an_unjudged_claim_is_unchecked()
    {
        var result = SupportCheck.Apply(
            Contract(C("supported claim", "e1"), C("partial claim", "e2"), C("unsupported claim", "e3"), C("unjudged claim", "e1")),
            [new ClaimVerdict(0, SupportVerdict.Supported, "says so"), new ClaimVerdict(1, SupportVerdict.Partial, "only half"), new ClaimVerdict(2, SupportVerdict.Unsupported, "about something else")]);

        Assert.Equal(["supported claim", "partial claim", "unjudged claim"], result.Claims.Select(c => c.Text));
        Assert.Equal([ClaimSupport.Supported, ClaimSupport.Partial, ClaimSupport.Unchecked], result.Claims.Select(c => c.Support));
        Assert.Equal(0.4, result.Claims[1].Confidence, 3);
        Assert.Equal(0.8, result.Claims[0].Confidence, 3);
        Assert.Equal(["Earlier question.", "Partly supported by its citation: partial claim (only half)", "Unsupported by its citation: unsupported claim (about something else)"], result.OpenQuestions);
        Assert.Equal(["e1", "e2"], result.Evidence.Select(e => e.Id));
        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal("The summary.", result.Summary);
    }

    [Fact]
    public void Evidence_another_claim_still_cites_stays()
    {
        var result = SupportCheck.Apply(Contract(C("bad", "e1"), C("good", "e1")), [new ClaimVerdict(0, SupportVerdict.Unsupported, ""), new ClaimVerdict(1, SupportVerdict.Supported, "")]);
        Assert.Equal(["e1"], result.Evidence.Select(e => e.Id));
        Assert.Contains("Unsupported by its citation: bad", result.OpenQuestions);
    }

    [Fact]
    public void All_claims_unsupported_leaves_a_completed_result_with_no_claims()
    {
        var result = SupportCheck.Apply(Contract(C("a", "e1"), C("b", "e2")), [new ClaimVerdict(0, SupportVerdict.Unsupported, "x"), new ClaimVerdict(1, SupportVerdict.Unsupported, "y")]);
        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Empty(result.Claims);
        Assert.Empty(result.Evidence);
        Assert.Equal(3, result.OpenQuestions.Count);
        Assert.Equal("The summary.", result.Summary);
    }

    [Fact]
    public void When_the_check_cannot_run_every_claim_is_unchecked_with_one_question()
    {
        var result = SupportCheck.Unavailable(Contract(C("a", "e1"), C("b", "e2")), "gateway down");
        Assert.All(result.Claims, c => Assert.Equal(ClaimSupport.Unchecked, c.Support));
        Assert.Equal(2, result.Claims.Count);
        Assert.Equal(["Earlier question.", "Support check unavailable: gateway down"], result.OpenQuestions);
    }

    [Fact]
    public void A_partly_supported_claim_is_not_retained_and_a_supported_one_is()
    {
        var partial = new Claim("half", ["e1"], 0.4, ClaimSupport.Partial);
        var supported = new Claim("whole", ["e1"], 0.8, ClaimSupport.Supported);
        var selection = RetainableClaims.From(Contract(partial, supported));
        Assert.Equal(["whole"], selection.Claims.Select(c => c.Text));
        Assert.Contains(selection.Skipped, s => s.StartsWith("partly supported: half", StringComparison.Ordinal));
    }

    private static string Verdicts(string verdict) => $$"""{"verdicts":[{"claim":1,"verdict":"{{verdict}}","reason":"because"}]}""";

    private static async Task<(ResultContract Result, ScriptedRuntime Runtime)> Run(string judgeReply, bool check)
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        // The first generate call is intake's; the judge's call gets the next one.
        var runtime = new ScriptedRuntime(Runs.WorkerReply, ScriptedRuntime.Spec(), judgeReply);
        var orchestrator = new Orchestrator(Runs.Profile(root.Path) with { SupportCheck = check }, runtime, "2.0.16", Repo.Root,
            new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), new Dictionary<string, int>());
        return (await orchestrator.RunAsync(Runs.CheapRequest(repo), CancellationToken.None), runtime);
    }

    [Fact]
    public async Task A_run_marks_a_supported_claim()
    {
        var (r, runtime) = await Run(Verdicts("supported"), check: true);
        Assert.Equal(ResultStatus.Completed, r.Status);
        Assert.Equal(ClaimSupport.Supported, Assert.Single(r.Claims).Support);
        Assert.Equal(2, runtime.IntakePrompts.Count);
        Assert.Contains("The README says hello.", runtime.IntakePrompts.ToArray()[1], StringComparison.Ordinal);
        Assert.Contains("1: hello", runtime.IntakePrompts.ToArray()[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_moves_an_unsupported_claim_to_the_open_questions()
    {
        var (r, _) = await Run(Verdicts("unsupported"), check: true);
        Assert.Equal(ResultStatus.Completed, r.Status);
        Assert.Empty(r.Claims);
        Assert.Contains(r.OpenQuestions, q => q.StartsWith("Unsupported by its citation: The README says hello.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_the_check_off_a_run_makes_no_extra_call_and_marks_nothing()
    {
        var (r, runtime) = await Run(Verdicts("unsupported"), check: false);
        Assert.Single(runtime.IntakePrompts);
        Assert.Null(Assert.Single(r.Claims).Support);
        Assert.Empty(r.OpenQuestions);
    }

    [Fact]
    public async Task A_judge_that_answers_with_nonsense_leaves_the_claims_unchecked()
    {
        var (r, runtime) = await Run("I could not decide.", check: true);
        Assert.Equal(ResultStatus.Completed, r.Status);
        Assert.Equal(ClaimSupport.Unchecked, Assert.Single(r.Claims).Support);
        Assert.Single(r.OpenQuestions);
        Assert.StartsWith("Support check unavailable:", r.OpenQuestions[0], StringComparison.Ordinal);
        Assert.Equal(3, runtime.IntakePrompts.Count);
    }
}
