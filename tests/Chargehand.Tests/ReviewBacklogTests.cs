using Chargehand.Automation;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0084: new driven starts wait while the pull requests chargehand opened and nobody reviewed reach <c>max_review_backlog</c>.</summary>
public class ReviewBacklogTests
{
    private static AutomationGitHub Settings(int backlog = 3, int concurrent = 5) =>
        new(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, [IntakeRig.Author], MaxConcurrent: concurrent, MaxReviewBacklog: backlog);

    private static void Pull(IntakeRig rig, int number, string branch = "chargehand/issue-9") =>
        rig.GitHub.OpenPulls.Add(new ReviewablePull(number, "t", "", "chargehand[bot]", "abcdef0123", true, branch));

    [Fact]
    public async Task Pull_requests_awaiting_review_at_the_limit_hold_a_new_start_with_a_reason_and_a_merge_admits_it()
    {
        using var rig = new IntakeRig(Settings());
        foreach (var n in new[] { 101, 102, 103 })
            Pull(rig, n, $"chargehand/issue-{n}");
        rig.Labeled(7);
        await rig.Poll();
        await rig.Poll();

        Assert.Empty(rig.Starter.Started);
        var waiting = Assert.Single(rig.State.Waiting);
        Assert.Equal(("review_backlog", false), (waiting.Reason, rig.State.Handled.Contains(waiting.Key)));
        Assert.Contains("3 pull requests", waiting.Detail, StringComparison.Ordinal);
        Assert.Single(rig.GitHub.Posted, p => p.Issue == 7 && p.Body.Contains("await review", StringComparison.Ordinal));

        rig.GitHub.OpenPulls.RemoveAll(p => p.Number == 101);
        await rig.Poll();

        Assert.Equal("issue-7", Assert.Single(rig.Starter.Started).Driven!.Tasks.Single().Id);
        Assert.Empty(rig.State.Waiting);
    }

    [Fact]
    public async Task A_reviewed_pull_request_a_foreign_branch_and_a_limit_of_zero_do_not_hold_a_start()
    {
        using var rig = new IntakeRig(Settings(backlog: 2));
        Pull(rig, 101);
        Pull(rig, 102);
        Pull(rig, 103, "feature/mine");
        rig.GitHub.Reviewers[102] = [IntakeRig.Author];
        rig.Labeled(7);
        await rig.Poll();

        Assert.Single(rig.Starter.Started);

        using var off = new IntakeRig(Settings(backlog: 0));
        for (var n = 101; n < 110; n++)
            Pull(off, n);
        off.Labeled(7);
        await off.Poll();

        Assert.Single(off.Starter.Started);
    }
}
