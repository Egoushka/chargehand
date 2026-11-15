using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>Subgate shares the subscription between interactive sessions and the pool: a hold keeps a start queued until its retry time.</summary>
public class SubgateHoldTests
{
    private static AutomationGitHub Settings() =>
        new(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, [IntakeRig.Author], MaxConcurrent: 5);

    [Fact]
    public async Task A_hold_keeps_the_start_queued_with_its_reason_and_it_starts_after_the_retry()
    {
        using var rig = new IntakeRig(Settings());
        var retry = rig.Now.AddMinutes(30);
        rig.SubgateHold = _ => Task.FromResult<DateTimeOffset?>(rig.Now < retry ? retry : null);
        rig.Labeled(1);
        await rig.Poll();

        Assert.Empty(rig.Starter.Started);
        var waiting = Assert.Single(rig.State.Waiting);
        Assert.Equal("subscription share", waiting.Reason);

        rig.Time.Advance(TimeSpan.FromMinutes(31));
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
        Assert.Empty(rig.State.Waiting);
    }

    [Fact]
    public async Task No_hold_starts_at_once()
    {
        using var rig = new IntakeRig(Settings());
        rig.SubgateHold = _ => Task.FromResult<DateTimeOffset?>(null);
        rig.Labeled(1);
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
    }
}
