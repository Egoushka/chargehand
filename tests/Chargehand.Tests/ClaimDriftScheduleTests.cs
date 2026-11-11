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

        dir.Write("docs/guide/x.md", "stale <!-- cites: src/A.cs#Beta -->\nalso <!-- cites: src/A.cs#Gamma -->\n");
        await rig.Wake(TimeSpan.FromDays(1));
        var second = rig.Notifier.Posted[^1].Body;
        Assert.Equal(2, rig.Notifier.Posted.Count);
        Assert.Contains("docs/guide/x.md:2: symbol-missing src/A.cs#Gamma", second, StringComparison.Ordinal);
        Assert.DoesNotContain("#Beta", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Claim_drift_reports_a_finding_again_when_it_was_fixed_and_broke_again()
    {
        using var dir = Repo("stale <!-- cites: src/A.cs#Beta -->\n");
        using var rig = new ScheduleRig(Entry(dir));
        await rig.Tick();
        await rig.Wake(TimeSpan.FromHours(6));
        dir.Write("docs/guide/x.md", "fixed <!-- cites: src/A.cs#Alpha -->\n");
        await rig.Wake(TimeSpan.FromDays(1));
        Assert.Single(rig.Notifier.Posted);

        dir.Write("docs/guide/x.md", "stale <!-- cites: src/A.cs#Beta -->\n");
        await rig.Wake(TimeSpan.FromDays(1));
        Assert.Equal(2, rig.Notifier.Posted.Count);
        Assert.Contains("#Beta", rig.Notifier.Posted[^1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Claim_drift_with_an_unreadable_guide_warns_and_leaves_the_other_entries_running()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var broken = Repo("ok <!-- cites: src/A.cs#Alpha -->\n");
        using var other = Repo("stale <!-- cites: src/A.cs#Beta -->\n");
        var guide = Path.Combine(broken.Path, "docs/guide");
        File.SetUnixFileMode(guide, UnixFileMode.None);
        try
        {
            using var rig = new ScheduleRig(Entry(broken) with { Id = "broken" }, Entry(other) with { Id = "other" });
            await rig.Tick();
            await rig.Wake(TimeSpan.FromHours(6));

            Assert.Contains(rig.Warnings, w => w.StartsWith("schedules: broken:", StringComparison.Ordinal));
            Assert.Single(rig.Notifier.Posted);
        }
        finally
        {
            File.SetUnixFileMode(guide, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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
