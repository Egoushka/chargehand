using Chargehand.Automation;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>The <c>claim-drift</c> schedule template: a report, no model call, each finding posted once.</summary>
public class ClaimDriftScheduleTests
{
    private static ScheduleEntry Entry(TempDir dir) => new("drift", "daily 04:00", Template: ScheduleTemplates.ClaimDrift, Repository: new AutomationRepository(dir.Path));

    private static TempDir Repo(string guide)
    {
        var dir = new TempDir();
        dir.Write("src/A.cs", "class Alpha {}\n");
        dir.Write("docs/guide/x.md", guide);
        return dir;
    }

    [Fact]
    public async Task Claim_drift_posts_findings_and_starts_no_run()
    {
        using var dir = Repo("stale <!-- cites: src/A.cs#Beta -->\n");
        using var rig = new ScheduleRig(Entry(dir));
        await rig.Tick();
        await rig.Wake(TimeSpan.FromHours(6));

        var (url, title, body) = Assert.Single(rig.Notifier.Posted);
        Assert.Equal(ScheduleRig.Url, url);
        Assert.Equal("chargehand claim drift 2026-01-01", title);
        Assert.Contains("docs/guide/x.md:1: symbol-missing src/A.cs#Beta", body, StringComparison.Ordinal);
        Assert.Empty(rig.Starter.Started);
    }

    [Fact]
    public async Task Claim_drift_reports_a_finding_once_across_two_occurrences()
    {
        using var dir = Repo("stale <!-- cites: src/A.cs#Beta -->\n");
        using var rig = new ScheduleRig(Entry(dir));
        await rig.Tick();
        await rig.Wake(TimeSpan.FromHours(6));
        await rig.Wake(TimeSpan.FromDays(1));
        Assert.Single(rig.Notifier.Posted);
    }

    [Fact]
    public async Task Claim_drift_posts_nothing_when_the_citations_hold()
    {
        using var dir = Repo("ok <!-- cites: src/A.cs#Alpha -->\n");
        using var rig = new ScheduleRig(Entry(dir));
        await rig.Tick();
        await rig.Wake(TimeSpan.FromHours(6));
        Assert.Empty(rig.Notifier.Posted);
        Assert.Empty(rig.Starter.Started);
    }
}
