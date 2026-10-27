using Chargehand.Automation;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0084: a start while a running task's rate-limit pause has not reset warns by default and waits when <c>subscription_block</c> is on.</summary>
public class SubscriptionHeadroomTests
{
    private static AutomationGitHub Settings(bool block) =>
        new(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, [IntakeRig.Author], MaxConcurrent: 5, SubscriptionBlock: block);

    [Fact]
    public async Task By_default_a_pause_is_warned_about_once_and_the_start_goes_on()
    {
        using var rig = new IntakeRig(Settings(block: false));
        rig.RateLimitUntil = rig.Now.AddHours(2);
        rig.Labeled(1);
        rig.Labeled(2, "alice", 5);
        await rig.Poll();
        await rig.Poll();

        Assert.Equal(["issue-1", "issue-2"], rig.Starter.Started.Select(r => r.Driven!.Tasks.Single().Id));
        var warning = Assert.Single(rig.Warnings, w => w.Contains("rate-limit pause", StringComparison.Ordinal));
        Assert.Contains("subscription_block", warning, StringComparison.Ordinal);
        Assert.Empty(rig.State.Waiting);
    }

    [Fact]
    public async Task With_block_on_a_start_waits_with_its_reason_and_starts_after_the_pause_resets()
    {
        using var rig = new IntakeRig(Settings(block: true));
        rig.RateLimitUntil = rig.Now.AddHours(2);
        rig.Labeled(1);
        await rig.Poll();

        Assert.Empty(rig.Starter.Started);
        var waiting = Assert.Single(rig.State.Waiting);
        Assert.Equal(("subscription", false), (waiting.Reason, rig.State.Handled.Contains(waiting.Key)));
        Assert.Single(rig.GitHub.Posted, p => p.Issue == 1 && p.Body.Contains("rate-limit", StringComparison.Ordinal));

        rig.Time.Advance(TimeSpan.FromHours(3));
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
        Assert.Empty(rig.State.Waiting);
    }

    [Fact]
    public async Task No_pause_no_warning()
    {
        using var rig = new IntakeRig(Settings(block: true));
        rig.Labeled(1);
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
        Assert.Empty(rig.Warnings);
    }
}
