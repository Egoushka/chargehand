using Chargehand.Automation;

namespace Chargehand.Tests;

public class SweepFilterTests
{
    private static IntakeIssue Issue(params string[] labels) =>
        new(5, "Title", "Body", "someone", false, labels, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Parses_blockers()
    {
        Assert.Equal([new IssueRef("owner/app", 452)], BlockerRefs.Parse("**Blocked by** #452: add the label once it merges", "owner/app"));
        Assert.Equal([new IssueRef("owner/lib", 7)], BlockerRefs.Parse("Depends on owner/lib#7", "owner/app"));
        Assert.Empty(BlockerRefs.Parse("See #3", "owner/app"));
    }

    [Fact]
    public void Parses_several_refs_in_one_sentence_and_stops_at_its_end()
    {
        var refs = BlockerRefs.Parse("Do it after #1 and owner/my.lib#2, #1 again. Related: #9.\nblocked by #4", "owner/app");
        Assert.Equal([new IssueRef("owner/app", 1), new IssueRef("owner/my.lib", 2), new IssueRef("owner/app", 4)], refs);
    }

    [Fact]
    public void Parses_list_style_blockers() =>
        Assert.Equal([new IssueRef("owner/app", 12), new IssueRef("owner/app", 13)], BlockerRefs.Parse("Blocked by:\n- #12\n* #13\n\n- #14\nnot a list #15", "owner/app"));

    [Fact]
    public void Parses_inline_forms()
    {
        Assert.Equal([new IssueRef("owner/app", 12)], BlockerRefs.Parse("after #12 merges", "owner/app"));
        Assert.Equal([new IssueRef("owner/app", 3), new IssueRef("owner/app", 4)], BlockerRefs.Parse("depends on #3 and #4", "owner/app"));
        Assert.Equal([new IssueRef("owner/lib", 7)], BlockerRefs.Parse("blocked by owner/lib#7", "owner/app"));
        Assert.Empty(BlockerRefs.Parse("after it merges\n- #5", "owner/app"));
    }

    [Fact]
    public void A_missing_blocker_still_parses_as_a_ref() =>
        Assert.Equal([new IssueRef("owner/app", 99)], BlockerRefs.Parse("blocked by #99", "owner/app"));

    [Fact]
    public void Does_not_match_inside_words_or_without_keyword() =>
        Assert.Empty(BlockerRefs.Parse("Afterwards see #3; thereafter x#4", "owner/app"));

    [Theory]
    [InlineData("you", SweepSkip.Owner)]
    [InlineData("in-progress", SweepSkip.InProgress)]
    [InlineData(SweepLabels.Skip, SweepSkip.Skip)]
    public void Skips_owner_and_in_progress(string label, SweepSkip expected) =>
        Assert.Equal(expected, SweepFilter.Skip(Issue(label), false, false));

    [Fact]
    public void Skips_claimed_and_queued_and_otherwise_none()
    {
        Assert.Equal(SweepSkip.Claimed, SweepFilter.Skip(Issue(), true, true));
        Assert.Equal(SweepSkip.AlreadyQueued, SweepFilter.Skip(Issue(), false, true));
        Assert.Equal(SweepSkip.None, SweepFilter.Skip(Issue("bug"), false, false));
    }
}
