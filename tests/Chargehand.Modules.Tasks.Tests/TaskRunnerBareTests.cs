using Chargehand.Contracts;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;
using static Chargehand.Tests.DrivenFakes;

namespace Chargehand.Modules.Tasks.Tests;

/// <summary>The benchmark's bare arm (ADR 0080) as a task runs it: a plain session, one handover that skips the tests and the pull request, and no salvage or second session.</summary>
public class TaskRunnerBareTests
{
    private static SessionOutcome Outcome(SessionStatus status, string reason = "", int commits = 0, bool bare = true) =>
        new(status, reason, 9, 1000, 200, 4000, null, 0, [], commits > 0, Commits: commits, Bare: bare);

    private static TaskRunnerRig Rig(SessionOutcome outcome) => new(new FakeVolumes(_ => outcome.Status, (_, _) => outcome), bare: true);

    [Fact]
    public async Task A_bare_task_gets_a_plain_task_file_and_its_branch_is_handed_over_as_bare()
    {
        using var rig = Rig(Outcome(SessionStatus.Completed, commits: 2));

        var outcome = await rig.RunAsync("Add a retry");

        Assert.Equal(TaskState.Completed, outcome.State);
        var task = Assert.Single(rig.Volumes.Tasks);
        Assert.True(task.Bare);
        Assert.Equal("Add a retry", task.Goal);
        Assert.Equal((null, null, null, null, null, 0), (task.Governor, task.Environment, task.Acceptance, task.ReminderMinutes, task.ReminderTurns, task.CheckRetries));
        Assert.True(Assert.Single(rig.Handover.Inputs).Bare);
    }

    [Fact]
    public async Task A_bare_session_that_left_no_commits_is_a_task_that_stopped_with_nothing_handed_over()
    {
        using var rig = Rig(Outcome(SessionStatus.Completed));

        var outcome = await rig.RunAsync("This cannot be done");

        Assert.Equal((TaskState.Failed, ErrorCode.InvalidResult), (outcome.State, outcome.Error));
        Assert.Empty(rig.Handover.Inputs);
    }

    [Fact]
    public async Task A_bare_session_that_was_stopped_is_neither_continued_nor_salvaged()
    {
        using var rig = Rig(Outcome(SessionStatus.Stalled, "turn_limit", commits: 3));

        var outcome = await rig.RunAsync("Add a retry");

        Assert.Equal((TaskState.Failed, ErrorCode.SessionStalled), (outcome.State, outcome.Error));
        Assert.Empty(rig.Handover.Inputs);
        Assert.Single(rig.Volumes.Tasks);
    }

    [Fact]
    public async Task A_session_image_from_before_the_arm_fails_the_task_instead_of_passing_for_the_control()
    {
        using var rig = Rig(Outcome(SessionStatus.Completed, commits: 2, bare: false));

        var outcome = await rig.RunAsync("Add a retry");

        Assert.Equal((TaskState.Failed, ErrorCode.SessionFailed), (outcome.State, outcome.Error));
        Assert.Contains("image predates bare arm", outcome.Detail);
        Assert.Empty(rig.Handover.Inputs);
    }

    [Fact]
    public async Task A_task_of_an_ordinary_batch_is_not_bare()
    {
        using var rig = new TaskRunnerRig();

        await rig.RunAsync("Add a retry");

        Assert.False(Assert.Single(rig.Volumes.Tasks).Bare);
        Assert.False(Assert.Single(rig.Handover.Inputs).Bare);
    }
}
