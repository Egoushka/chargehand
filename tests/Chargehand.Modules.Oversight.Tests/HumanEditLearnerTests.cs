using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Oversight.Tests;

/// <summary>Issue #452: CLAUDE.md's own rule for a person ("a mistake an agent makes twice becomes a line in this file") applied to chargehand's human fixes. The second commit of a
/// category is the one that is written up; the first is not (a fluke) and the third is not either (it already got a proposal).</summary>
public class HumanEditLearnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

    private static HumanEditCommit Commit(string category, string sha = "sha1", string prUrl = "https://example.test/o/r/pull/1") =>
        new(Now, "run-1", prUrl, sha, category, "a human fix");

    [Fact]
    public void The_first_commit_of_a_category_does_not_propose() => Assert.Null(HumanEditLearner.Propose([], Commit(HumanEditCategory.Lint), Now));

    [Fact]
    public void The_second_commit_of_a_category_proposes()
    {
        var first = Commit(HumanEditCategory.Lint, "sha1", "https://example.test/o/r/pull/1");
        var second = Commit(HumanEditCategory.Lint, "sha2", "https://example.test/o/r/pull/2");

        var proposal = HumanEditLearner.Propose([first], second, Now);

        Assert.NotNull(proposal);
        Assert.Equal(HumanEditCategory.Lint, proposal.Category);
        Assert.Equal(2, proposal.Occurrences);
        Assert.Equal([first.PrUrl, second.PrUrl], proposal.Examples);
        Assert.Equal(Now, proposal.At);
    }

    [Fact]
    public void The_third_commit_of_a_category_does_not_propose_again()
    {
        var history = new[] { Commit(HumanEditCategory.Lint, "sha1"), Commit(HumanEditCategory.Lint, "sha2") };

        Assert.Null(HumanEditLearner.Propose(history, Commit(HumanEditCategory.Lint, "sha3"), Now));
    }

    [Fact]
    public void A_second_commit_of_a_different_category_does_not_cross_with_the_first()
    {
        var history = new[] { Commit(HumanEditCategory.Lint, "sha1") };

        Assert.Null(HumanEditLearner.Propose(history, Commit(HumanEditCategory.MissingImport, "sha2"), Now));
    }

    [Fact]
    public void Other_never_proposes()
    {
        var history = new[] { Commit(HumanEditCategory.Other, "sha1") };

        Assert.Null(HumanEditLearner.Propose(history, Commit(HumanEditCategory.Other, "sha2"), Now));
    }

    [Theory]
    [InlineData(HumanEditCategory.Lint)]
    [InlineData(HumanEditCategory.MissingImport)]
    [InlineData(HumanEditCategory.UnboundedInput)]
    [InlineData(HumanEditCategory.CiConfig)]
    public void Every_recognised_category_has_its_own_proposal_text(string category)
    {
        var proposal = HumanEditLearner.Propose([Commit(category, "sha1")], Commit(category, "sha2"), Now);

        Assert.NotNull(proposal);
        Assert.False(string.IsNullOrWhiteSpace(proposal.Title));
        Assert.Contains("2", proposal.Body, StringComparison.Ordinal);
    }
}
