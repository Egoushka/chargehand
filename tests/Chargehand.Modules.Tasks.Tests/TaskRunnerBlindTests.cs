using Chargehand.Contracts;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;
using static Chargehand.Tests.DrivenFakes;

namespace Chargehand.Modules.Tasks.Tests;

/// <summary>Issue 789: the blind sibling hands over a bundle of tests, the implementer's handover carries it, and a failing blind test continues the session.</summary>
public class TaskRunnerBlindTests
{
    private const string Name = "DiscountTests.Applies_at_exactly_100";

    private static TaskRunnerRig Rig() => new(new FakeVolumes(_ => SessionStatus.Completed, (_, _) =>
        new SessionOutcome(SessionStatus.Completed, "", 9, 1000, 200, 4000, null, 0, [], true, Commits: 1)));

    private static HandoverOutcome Failing(HandoverInput input) =>
        new(HandoverStatus.NotPushed, $"blind_tests_failed: 1 blind test(s) failed: {Name}", ErrorCode.VerificationFailed, null, null, null,
            new VerificationRecord(["dotnet", "test"], "detected", 1, "output", false, TimeSpan.FromSeconds(1), false, BlindTests: new BlindTestResult(2, 1, [Name], [])), ["src/A.cs"], [], []);

    [Fact]
    public async Task The_blind_task_hands_over_a_bundle_without_a_pull_request_and_the_implementer_carries_it()
    {
        using var rig = Rig();

        var (implementer, blind) = await rig.RunPairAsync("Discount orders of 100 or more");

        Assert.Equal(TaskState.Completed, implementer.State);
        Assert.True(blind.State == TaskState.Completed, blind.Detail);
        var blindInput = Assert.Single(rig.Handover.Inputs, i => i.BlindTask);
        Assert.True(blindInput.Bare);
        var own = Assert.Single(rig.Handover.Inputs, i => !i.BlindTask);
        Assert.Single(own.BlindBundles!);
    }

    [Fact]
    public async Task A_refused_blind_task_leaves_the_implementer_to_carry_on_without_blind_tests()
    {
        using var rig = Rig();
        rig.Handover.Refuse = (_, input) => input.BlindTask
            ? new HandoverOutcome(HandoverStatus.NotPushed, "blind_not_tests: x", ErrorCode.VerificationFailed, null, null, null, null, ["src/A.cs"], [], [])
            : null;

        var (implementer, _) = await rig.RunPairAsync("Discount orders of 100 or more");

        Assert.Equal(TaskState.Completed, implementer.State);
        Assert.Null(Assert.Single(rig.Handover.Inputs, i => !i.BlindTask).BlindBundles);
    }

    [Fact]
    public async Task A_failing_blind_test_continues_the_session_with_the_failing_test_names()
    {
        using var rig = Rig();
        rig.Handover.Refuse = (_, input) => !input.BlindTask && !input.Partial && input.BlindBundles is { Count: > 0 } && rig.Handover.Inputs.Count(i => !i.BlindTask) == 1 ? Failing(input) : null;

        var (implementer, _) = await rig.RunPairAsync("Discount orders of 100 or more");

        Assert.Equal(TaskState.Completed, implementer.State);
        Assert.Contains(rig.Volumes.Tasks, t => t.Goal.Contains(Name, StringComparison.Ordinal));
        Assert.Equal(2, rig.Handover.Inputs.Count(i => !i.BlindTask));
    }

    [Fact]
    public async Task After_the_continuations_the_work_is_handed_over_as_partial()
    {
        using var rig = Rig();
        rig.Handover.Refuse = (_, input) => !input.BlindTask && !input.Partial ? Failing(input) : null;

        var (implementer, _) = await rig.RunPairAsync("Discount orders of 100 or more");

        Assert.NotNull(implementer.PrUrl);
        Assert.True(rig.Handover.Inputs.Last(i => !i.BlindTask).Partial);
    }
}
