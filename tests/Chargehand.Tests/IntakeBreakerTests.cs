using Chargehand.Automation;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0084: the start-rate breaker and the per-repository failure breaker pause new starts; running work, rounds and answers go on.</summary>
public class IntakeBreakerTests
{
    private static AutomationGitHub Settings(int perHour = 30, int cooldown = 120) =>
        new(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, [IntakeRig.Author], MaxConcurrent: 10, MaxReviewBacklog: 0, MaxStartsPerHour: perHour, BreakerCooldownMinutes: cooldown);

    private static TaskOutcome Fail(int issue) => new($"issue-{issue}", TaskState.Failed, "task-run", "the tests fail", ErrorCode.SessionFailed);

    [Fact]
    public async Task More_starts_than_the_hourly_limit_pause_new_starts_with_one_warning_until_the_hour_passes()
    {
        using var rig = new IntakeRig(Settings(perHour: 2));
        rig.Labeled(1);
        rig.Labeled(2, "alice", 5);
        rig.Labeled(3, "alice", 6);
        await rig.Poll();
        await rig.Poll();

        Assert.Equal(["issue-1", "issue-2"], rig.Starter.Started.Select(r => r.Driven!.Tasks.Single().Id));
        var waiting = Assert.Single(rig.State.Waiting);
        Assert.Equal(("breaker", 3, false), (waiting.Reason, waiting.Issue, rig.State.Handled.Contains(waiting.Key)));
        Assert.Contains("2 starts in the last hour", waiting.Detail, StringComparison.Ordinal);
        Assert.Single(rig.Warnings, w => w.Contains("start-rate breaker", StringComparison.Ordinal));

        rig.Time.Advance(TimeSpan.FromMinutes(61));
        await rig.Poll();

        Assert.Equal("issue-3", rig.Starter.Started.Last().Driven!.Tasks.Single().Id);
        Assert.Empty(rig.State.Waiting);
    }

    [Fact]
    public async Task Three_failed_batches_in_a_row_pause_the_repository_until_the_cooldown_and_a_success_ends_the_streak()
    {
        using var rig = new IntakeRig(Settings());
        for (var i = 1; i <= 3; i++)
        {
            rig.Labeled(i, "alice", 10 + i);
            await rig.Poll();
            rig.Log.End($"run-{i}", Fail(i));
            await rig.Poll();
        }
        rig.Labeled(4, "alice", 20);
        await rig.Poll();

        Assert.Equal(3, rig.Starter.Started.Count);
        var waiting = Assert.Single(rig.State.Waiting);
        Assert.Equal(("breaker", 4), (waiting.Reason, waiting.Issue));
        Assert.Contains("3 failed batches in a row", waiting.Detail, StringComparison.Ordinal);
        Assert.Single(rig.Warnings, w => w.Contains("failure breaker", StringComparison.Ordinal));

        rig.Time.Advance(TimeSpan.FromMinutes(121));
        await rig.Poll();

        Assert.Equal("issue-4", rig.Starter.Started.Last().Driven!.Tasks.Single().Id);
        rig.Log.End("run-4", IntakeRig.Pr("issue-4"));
        await rig.Poll();
        Assert.Equal(IntakePhase.Done, rig.State.Tasks.Last().Phase);
    }

    [Fact]
    public async Task A_success_between_failures_keeps_the_streak_short()
    {
        using var rig = new IntakeRig(Settings());
        var outcomes = new[] { Fail(1), IntakeRig.Pr("issue-2"), Fail(3) };
        for (var i = 1; i <= 3; i++)
        {
            rig.Labeled(i, "alice", 10 + i);
            await rig.Poll();
            rig.Log.End($"run-{i}", outcomes[i - 1]);
            await rig.Poll();
        }
        rig.Labeled(4, "alice", 20);
        await rig.Poll();

        Assert.Equal(4, rig.Starter.Started.Count);
        Assert.Empty(rig.State.Waiting);
    }
}
