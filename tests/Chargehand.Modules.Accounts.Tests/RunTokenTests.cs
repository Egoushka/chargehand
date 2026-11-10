using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;

namespace Chargehand.Modules.Accounts.Tests;

/// <summary>ADR 0039: a driven session calls chargehand back for research and review. It holds a token that opens two presets and nothing else.</summary>
public class RunTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static RunTokenClaims Claims(decimal? usd = null, long? tokens = null, DateTimeOffset? expires = null) =>
        new("run-parent", "/srv/repo", "abc1234", usd, tokens, expires ?? Now.AddHours(1));

    [Fact]
    public void A_token_round_trips_its_claims()
    {
        var service = new RunTokenService(new byte[32]);
        var token = service.Issue(Claims(2.5m, 500_000), Now);
        Assert.StartsWith("chr1.", token);
        Assert.Equal(Claims(2.5m, 500_000), service.Validate(token, Now));
    }

    [Fact]
    public void A_changed_token_a_foreign_key_and_an_expired_token_are_all_refused()
    {
        var service = new RunTokenService(new byte[32]);
        var token = service.Issue(Claims(), Now);
        var parts = token.Split('.');
        Assert.Null(service.Validate($"{parts[0]}.{parts[1]}.{parts[2][..^2]}AA", Now));                             // signature changed
        Assert.Null(service.Validate($"{parts[0]}.{parts[1][..^2]}AA.{parts[2]}", Now));                             // payload changed
        Assert.Null(new RunTokenService(Enumerable.Repeat((byte)7, 32).ToArray()).Validate(token, Now));            // another server's key
        Assert.Null(service.Validate(token, Now.AddHours(2)));                                                      // expired
        foreach (var junk in new[] { "", "chr1", "chr1..", "chr1.a.b", "x.y.z", "chr1." + new string('a', 100_000), "Bearer chr1.a.b" })
            Assert.Null(service.Validate(junk, Now));
    }

    [Fact]
    public void Two_tokens_for_different_claims_differ()
    {
        var service = new RunTokenService(new byte[32]);
        Assert.NotEqual(service.Issue(Claims(), Now), service.Issue(Claims() with { Commit = "def5678" }, Now));
    }

    private static RunRequest Request(string preset = "default", RepositoryRef? repo = null, decimal? budget = null, RequestDriven? driven = null) =>
        new("request/v1", "Answer without changing anything.", new RequestContext(false, preset, budget, repo ?? new RepositoryRef("/srv/repo", "abc1234")), Driven: driven);

    [Theory]
    [InlineData("default")]
    [InlineData("review")]
    public void The_two_presets_on_the_pinned_commit_are_allowed(string preset) =>
        Assert.Null(RunScope.Apply(Claims(), Request(preset), new RunTokenLedger()).Refusal);

    [Theory]
    [InlineData("code")]
    [InlineData("driven")]
    [InlineData("cheap")]
    [InlineData("draft")]
    public void Any_other_preset_is_refused(string preset) =>
        Assert.Contains("preset", RunScope.Apply(Claims(), Request(preset), new RunTokenLedger()).Refusal);

    [Fact]
    public void Another_repository_another_commit_no_repository_and_a_nested_batch_are_refused()
    {
        var ledger = new RunTokenLedger();
        Assert.Contains("repository", RunScope.Apply(Claims(), Request(repo: new RepositoryRef("/srv/other", "abc1234")), ledger).Refusal);
        Assert.Contains("commit", RunScope.Apply(Claims(), Request(repo: new RepositoryRef("/srv/repo", "def5678")), ledger).Refusal);
        Assert.Contains("repository", RunScope.Apply(Claims(), Request() with { Context = Request().Context with { Repository = null } }, ledger).Refusal);
        Assert.Contains("batch", RunScope.Apply(Claims(), Request(driven: new RequestDriven([new DrivenTask("t", null, "g")])), ledger).Refusal);
    }

    [Fact]
    public void A_descendant_commit_is_kept_an_unknown_one_runs_on_the_task_commit_and_an_unrelated_one_is_refused()
    {
        var ledger = new RunTokenLedger();
        var later = Request(repo: new RepositoryRef("/srv/repo", "def5678"));
        Assert.Equal("def5678", RunScope.Apply(Claims(), later, ledger, CommitRelation.Descendant).Request!.Context.Repository!.Commit);
        // The session's own commit never left its container: the call runs on the task's commit.
        Assert.Equal("abc1234", RunScope.Apply(Claims(), later, ledger, CommitRelation.Unknown).Request!.Context.Repository!.Commit);
        Assert.Contains("commit", RunScope.Apply(Claims(), later, ledger, CommitRelation.Unrelated).Refusal);
    }


    [Fact]
    public void The_budget_of_a_call_is_capped_at_what_the_token_has_left()
    {
        var ledger = new RunTokenLedger();
        ledger.Charge("run-parent", usd: 1.0m, tokens: 0);
        Assert.Equal(1.5m, RunScope.Apply(Claims(usd: 2.5m), Request(), ledger).Request!.Context.BudgetUsd);          // none asked: the remainder
        Assert.Equal(1.5m, RunScope.Apply(Claims(usd: 2.5m), Request(budget: 9m), ledger).Request!.Context.BudgetUsd);  // too much asked: the remainder
        Assert.Equal(0.5m, RunScope.Apply(Claims(usd: 2.5m), Request(budget: 0.5m), ledger).Request!.Context.BudgetUsd); // less asked: kept
        Assert.Null(RunScope.Apply(Claims(), Request(), ledger).Request!.Context.BudgetUsd);                          // no cap: untouched
    }

    [Fact]
    public void A_token_over_its_dollar_or_token_cap_is_refused()
    {
        var ledger = new RunTokenLedger();
        ledger.Charge("run-parent", usd: 2.5m, tokens: 600_000);
        Assert.Contains("spend", RunScope.Apply(Claims(usd: 2.5m), Request(), ledger).Refusal);
        Assert.Contains("token", RunScope.Apply(Claims(tokens: 500_000), Request(), ledger).Refusal);
        Assert.Null(RunScope.Apply(Claims(usd: 9m, tokens: 9_000_000), Request(), ledger).Refusal);
        Assert.Null(RunScope.Apply(Claims(), Request(), new RunTokenLedger()).Refusal);
    }

    [Fact]
    public void Spend_is_kept_per_parent_run()
    {
        var ledger = new RunTokenLedger();
        ledger.Charge("a", 1m, 10);
        ledger.Charge("a", 1m, 10);
        ledger.Charge("b", 5m, 1);
        Assert.Equal((2m, 20L), ledger.Spent("a"));
        Assert.Equal((5m, 1L), ledger.Spent("b"));
        Assert.Equal((0m, 0L), ledger.Spent("c"));
    }
}
