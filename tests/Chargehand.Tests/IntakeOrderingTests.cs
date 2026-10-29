using Chargehand.Automation;
using Chargehand.Config;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>Issue #576: every waiting trigger is ranked the same way, highest first: a priority label on the issue, then source (a review round, then an answer, then a new issue), then
/// the issue's size label, then whoever has waited longer. GitHub, the batch starter, the run log and the clock are fakes.</summary>
public class IntakeOrderingTests
{
    private const string Branch = "chargehand/task-run";

    private static AutomationGitHub Settings() =>
        new(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, [IntakeRig.Author], MaxConcurrent: 1, MaxReviewBacklog: 0);

    [Fact]
    public async Task A_priority_label_outranks_source_which_outranks_a_plain_new_issue()
    {
        using var rig = new IntakeRig(Settings());
        await rig.Poll();

        // A finished task with a pull request, so a review comment on it can compete for the queue later.
        rig.Labeled(7, "alice", 41);
        await rig.Poll();
        rig.Log.TaskRun("task-run", "run-1", Branch);
        rig.Log.End("run-1", IntakeRig.Pr());
        rig.Pull(9);
        await rig.Poll();
        Assert.Equal(IntakePhase.Done, rig.State.Tasks.Single().Phase);

        // A task waiting for an answer, so a reply to it can compete too.
        rig.Labeled(20, "alice", 42);
        await rig.Poll();
        rig.Log.Asked("task-run-20", "Which policy?");
        rig.Log.End("run-2", new TaskOutcome("issue-20", TaskState.NeedsInput, "task-run-20", "asked"));
        await rig.Poll();
        Assert.Equal(IntakePhase.Asked, rig.State.Tasks.Single(t => t.Issue == 20).Phase);

        // Four triggers ready at once, one free slot: a review round, an answer, a new issue with a priority label, and a plain new issue.
        rig.Review(9, "alice", "Fix this.");
        rig.Comment(20, "alice", "Use exponential backoff.");
        rig.Labeled(30, "alice", 43, "priority:high");
        rig.Labeled(40, "alice", 44);
        await rig.Poll();

        // The priority label wins over every source, even a review round and an answer that have waited longer.
        Assert.Equal(3, rig.Starter.Started.Count);
        Assert.Equal("issue-30", rig.Starter.Started[2].Driven!.Tasks.Single().Id);
        Assert.Equal([9, 20, 40], rig.State.Waiting.Select(w => w.Issue).Order());

        // With priority tied, the review round goes first.
        rig.Log.End("run-3", IntakeRig.Pr("issue-30"));
        await rig.Poll();
        Assert.Equal(4, rig.Starter.Started.Count);
        Assert.Equal("pr-9", rig.Starter.Started[3].Driven!.Tasks.Single().Id);

        // Then the answer, before a new issue of the same priority.
        rig.Log.End("run-4", IntakeRig.Pr("pr-9"));
        await rig.Poll();
        Assert.Equal(5, rig.Starter.Started.Count);
        Assert.Equal("issue-20", rig.Starter.Started[4].Driven!.Tasks.Single().Id);

        // Finally the plain new issue, last of the four.
        rig.Log.End("run-5", IntakeRig.Pr("issue-20"));
        await rig.Poll();
        Assert.Equal(6, rig.Starter.Started.Count);
        Assert.Equal("issue-40", rig.Starter.Started[5].Driven!.Tasks.Single().Id);
    }
}
