using Chargehand.Automation;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>Issue #792: harvest work waits for the end of a subscription window that has quota left, starts <c>size:S</c> first, and stops at the day's cap.</summary>
public class QuotaHarvestTests
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(5);

    private static AutomationGitHub Settings(int perDay = 2) =>
        new(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, [IntakeRig.Author], MaxConcurrent: 10, MaxReviewBacklog: 0,
            Harvest: new AutomationHarvest(LeadMinutes: 30, MaxPerDay: perDay));

    /// <summary>A fake quota clock: windows of five hours from the rig's midnight, with quota left in each.</summary>
    private static QuotaWindow? Clock(DateTimeOffset now)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return new QuotaWindow(start + Window * ((now - start).Ticks / Window.Ticks + 1), true);
    }

    private static IEnumerable<string> Started(IntakeRig rig) => rig.Starter.Started.Select(r => r.Driven!.Tasks.Single().Id);

    [Fact]
    public async Task Harvest_work_starts_only_near_the_end_of_a_window_small_first_and_stops_at_the_daily_cap()
    {
        using var rig = new IntakeRig(Settings()) { QuotaWindow = Clock };
        rig.Labeled(1, "alice", 11, "priority:low", "size:L");
        rig.Labeled(2, "alice", 12, "priority:low", "size:S");
        rig.Labeled(3, "alice", 13, "priority:low", "size:M");
        rig.Labeled(4, "alice", 14);
        await rig.Poll();

        Assert.Equal(["issue-4"], Started(rig));
        Assert.Equal([1, 2, 3], rig.State.Waiting.Where(w => w.Reason == "harvest").Select(w => w.Issue).Order());

        rig.Time.Advance(TimeSpan.FromHours(4));
        await rig.Poll();
        Assert.Equal(["issue-4"], Started(rig));

        rig.Time.Advance(TimeSpan.FromMinutes(31));
        await rig.Poll();

        Assert.Equal(["issue-4", "issue-2", "issue-3"], Started(rig));
        var capped = Assert.Single(rig.State.Waiting);
        Assert.Equal(("harvest", 1), (capped.Reason, capped.Issue));
        Assert.Contains("2 harvested starts today (limit 2)", capped.Detail, StringComparison.Ordinal);

        // The next window's end is still the same day: the cap holds.
        rig.Time.Advance(TimeSpan.FromHours(5));
        await rig.Poll();
        Assert.Equal(3, rig.Starter.Started.Count);

        // A new UTC day, near the end of its first window (resets at 01:00): the day's cap is fresh.
        rig.Time.Advance(TimeSpan.FromHours(15));
        await rig.Poll();
        Assert.Equal(["issue-4", "issue-2", "issue-3", "issue-1"], Started(rig));
        Assert.All(rig.State.Tasks.Where(t => t.Issue != 4), t => Assert.True(t.Harvested));
    }

    [Fact]
    public async Task A_window_whose_quota_is_used_up_holds_harvest_work_even_near_its_reset()
    {
        using var rig = new IntakeRig(Settings()) { QuotaWindow = now => Clock(now)! with { QuotaLeft = false } };
        rig.Labeled(1, "alice", 11, "priority:low", "size:S");
        rig.Time.Advance(TimeSpan.FromMinutes(4 * 60 + 45));
        await rig.Poll();

        Assert.Empty(rig.Starter.Started);
        Assert.Contains("quota is used up", Assert.Single(rig.State.Waiting).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_harvester_the_label_starts_like_any_other()
    {
        using var rig = new IntakeRig(Settings() with { Harvest = null }) { QuotaWindow = Clock };
        rig.Labeled(1, "alice", 11, "priority:low");
        await rig.Poll();

        Assert.Equal(["issue-1"], Started(rig));
    }

    [Fact]
    public void The_reported_window_is_used_up_during_a_pause_and_projected_from_its_reset_after()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset? paused = null;
        var window = QuotaHarvest.Reported(Window, now => paused > now ? paused : null);

        Assert.Null(window(start));
        paused = start.AddHours(2);
        Assert.Equal(new QuotaWindow(start.AddHours(2), false), window(start.AddHours(1)));
        Assert.Equal(new QuotaWindow(start.AddHours(7), true), window(start.AddHours(3)));
        Assert.Equal(new QuotaWindow(start.AddHours(12), true), window(start.AddHours(8)));
    }
}
