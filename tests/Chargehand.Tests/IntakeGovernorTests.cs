using Chargehand.Automation;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0084, the executable acceptance of the desk spec: the checks together, a restart in the middle.</summary>
public class IntakeGovernorTests
{
    private static AutomationGitHub Settings(int concurrent = 4, int backlog = 3) =>
        new(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, [IntakeRig.Author], MaxConcurrent: concurrent, MaxReviewBacklog: backlog);

    private static void Pull(IntakeRig rig, int number) =>
        rig.GitHub.OpenPulls.Add(new ReviewablePull(number, "t", "", "chargehand[bot]", "abcdef0123", true, $"chargehand/issue-{number}"));

    [Fact]
    public async Task Twenty_seven_labelled_issues_with_four_slots_and_money_for_ten_start_ten_and_keep_seventeen_queued_with_a_reason()
    {
        using var rig = new IntakeRig(Settings(backlog: 0));
        rig.Starter.Allowance = 10;
        for (var n = 1; n <= 27; n++)
            rig.Labeled(n, "alice", 100 + n);

        for (var poll = 0; poll < 12; poll++)
        {
            if (poll == 4)
                rig.Restart();
            await rig.Poll();
            foreach (var task in rig.State.Tasks.Where(t => t.Phase == IntakePhase.Running))
                rig.Log.End(task.BatchRunId, IntakeRig.Pr(task.TaskId));
            rig.Time.Advance(TimeSpan.FromMinutes(2));
        }

        var state = rig.State;
        Assert.Equal(10, rig.Starter.Started.Select(r => r.Driven!.Tasks.Single().Id).Distinct().Count());
        Assert.Equal(17, state.Waiting.Count);
        Assert.All(state.Waiting, w => Assert.Equal(("admission", false), (w.Reason, state.Handled.Contains(w.Key))));
        Assert.All(state.Waiting, w => Assert.Contains("budget_exceeded", w.Detail, StringComparison.Ordinal));
        Assert.Equal(10, state.Handled.Count(k => k.StartsWith("label:", StringComparison.Ordinal)));

        rig.Starter.Allowance = null;
        for (var poll = 0; poll < 12 && state.Waiting.Count > 0; poll++)
        {
            await rig.Poll();
            foreach (var task in rig.State.Tasks.Where(t => t.Phase == IntakePhase.Running))
                rig.Log.End(task.BatchRunId, IntakeRig.Pr(task.TaskId));
            rig.Time.Advance(TimeSpan.FromMinutes(2));
            state = rig.State;
        }

        Assert.Equal(27, rig.Starter.Started.Select(r => r.Driven!.Tasks.Single().Id).Distinct().Count());
        Assert.Empty(rig.State.Waiting);
    }

    [Fact]
    public async Task Merging_one_pull_request_at_the_review_limit_admits_one_item_and_the_queue_survives_a_restart()
    {
        using var rig = new IntakeRig(Settings(concurrent: 10));
        foreach (var n in new[] { 101, 102, 103 })
            Pull(rig, n);
        rig.Labeled(1);
        rig.Labeled(2, "alice", 5);
        await rig.Poll();
        rig.Restart();
        await rig.Poll();

        Assert.Empty(rig.Starter.Started);
        Assert.Equal([1, 2], rig.State.Waiting.Select(w => w.Issue).Order());
        Assert.All(rig.State.Waiting, w => Assert.Equal("review_backlog", w.Reason));

        rig.GitHub.OpenPulls.RemoveAll(p => p.Number == 101);
        await rig.Poll();
        rig.Restart();
        await rig.Poll();

        Assert.Equal("issue-1", Assert.Single(rig.Starter.Started).Driven!.Tasks.Single().Id);
        var waiting = Assert.Single(rig.State.Waiting);
        Assert.Equal((2, "review_backlog"), (waiting.Issue, waiting.Reason));
    }
}
