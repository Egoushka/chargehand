using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Tasks.Api;
using static Chargehand.Tests.DrivenFakes;

namespace Chargehand.Modules.Tasks.Tests;

/// <summary>Issue 789: the blind session is isolated by construction. It gets the issue text and the base commit in a work volume of its own, and nothing the implementer made.</summary>
public class BlindIsolationTests
{
    private const string Issue = "Orders of 100 or more get a discount.";

    private static TaskRunnerRig Rig() => new(new FakeVolumes(_ => Chargehand.Modules.Sessions.Api.SessionStatus.Completed, (_, _) =>
        new Chargehand.Modules.Sessions.Api.SessionOutcome(Chargehand.Modules.Sessions.Api.SessionStatus.Completed, "", 9, 1000, 200, 4000, null, 0, [], true, Commits: 1)));

    [Fact]
    public async Task The_blind_session_gets_the_issue_text_alone_on_the_base_commit_and_none_of_the_implementers_work()
    {
        using var rig = Rig();

        await rig.RunPairAsync(Issue);

        var blindRun = Assert.Single(rig.Handover.Inputs, i => i.BlindTask).RunId;
        var implementerRun = Assert.Single(rig.Handover.Inputs, i => !i.BlindTask).RunId;
        Assert.NotEqual(blindRun, implementerRun);
        Assert.Equal(Issue, Assert.Single(rig.Volumes.Tasks, t => t.RunId == blindRun).Goal);
        Assert.Contains(rig.Engine.Calls, c => c.StartsWith($"prepare {blindRun} ", StringComparison.Ordinal) && c.EndsWith($" {TaskRunnerRig.Commit}", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Volumes.Seeds, s => s.RunId == blindRun);
        Assert.NotEqual(RunnerNames.Work(blindRun), RunnerNames.Work(implementerRun));
    }

    [Fact]
    public async Task A_pin_meant_for_the_implementer_never_reaches_the_blind_session()
    {
        using var rig = Rig();

        await rig.RunPairAsync(Issue, implementerNote: "Name the method ApplyDiscount.");

        var blindRun = Assert.Single(rig.Handover.Inputs, i => i.BlindTask).RunId;
        var implementerRun = Assert.Single(rig.Handover.Inputs, i => !i.BlindTask).RunId;
        Assert.Equal(Issue, Assert.Single(rig.Volumes.Tasks, t => t.RunId == blindRun).Goal);
        Assert.Contains("ApplyDiscount", Assert.Single(rig.Volumes.Tasks, t => t.RunId == implementerRun).Goal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_work_volume_of_the_blind_session_is_made_before_the_implementers_bundle_exists_and_never_reads_it()
    {
        using var rig = Rig();

        await rig.RunPairAsync(Issue);

        var implementerBundle = Path.Combine(Assert.Single(rig.Handover.Inputs, i => !i.BlindTask).OutDirectory, "chargehand.bundle");
        var blindRun = Assert.Single(rig.Handover.Inputs, i => i.BlindTask).RunId;
        Assert.DoesNotContain(rig.Volumes.Seeds, s => s.Path == implementerBundle);
        Assert.DoesNotContain(rig.Engine.Calls, c => c.Contains(blindRun, StringComparison.Ordinal) && c.Contains(implementerBundle, StringComparison.Ordinal));
    }
}
