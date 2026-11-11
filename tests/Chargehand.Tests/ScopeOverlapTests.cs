using Chargehand.Automation;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>Issue #737: a new start whose issue declares a scope that overlaps a running task or an open chargehand pull request waits with reason <c>overlap</c>.</summary>
public class ScopeOverlapTests
{
    private static AutomationGitHub Settings() =>
        new(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, [IntakeRig.Author], MaxConcurrent: 5);

    private static void Scoped(IntakeRig rig, int number, string scope)
    {
        var issue = rig.Labeled(number);
        rig.GitHub.Issues.Remove(issue);
        rig.GitHub.Issues.Add(issue with { Body = $"Do the thing.\n\nScope: {scope}" });
    }

    [Fact]
    public async Task Two_runs_with_overlapping_scope_start_one_and_hold_the_other_with_an_overlap_reason()
    {
        using var rig = new IntakeRig(Settings());
        Scoped(rig, 7, "src/Client/Retry.cs, docs/guide/client.md");
        await rig.Poll();
        Scoped(rig, 8, "`src/Client/`");
        Scoped(rig, 9, "src/Server/");
        await rig.Poll();

        Assert.Equal(["issue-7", "issue-9"], rig.Starter.Started.Select(s => s.Driven!.Tasks.Single().Id));
        var waiting = Assert.Single(rig.State.Waiting);
        Assert.Equal((8, "overlap"), (waiting.Issue, waiting.Reason));
        Assert.Contains("#7", waiting.Detail, StringComparison.Ordinal);
        Assert.Single(rig.GitHub.Posted, p => p.Issue == 8 && p.Body.Contains("overlaps", StringComparison.Ordinal));
        Assert.Equal(["src/Client/Retry.cs", "docs/guide/client.md"], rig.State.Tasks.Single(t => t.Issue == 7).Scope);
    }

    [Fact]
    public async Task An_open_chargehand_pull_request_that_touches_the_scope_holds_a_start_until_it_closes()
    {
        using var rig = new IntakeRig(Settings());
        rig.GitHub.OpenPulls.Add(new ReviewablePull(101, "t", "", "chargehand[bot]", "abcdef0123", true, "chargehand/issue-5"));
        rig.GitHub.OpenPulls.Add(new ReviewablePull(102, "t", "", "carol", "abcdef0124", false, "feature/mine"));
        rig.GitHub.PullFiles[101] = ["docs/guide/intake.md"];
        rig.GitHub.PullFiles[102] = ["src/a.cs"];
        Scoped(rig, 7, "docs/**");
        Scoped(rig, 8, "src/a.cs");
        await rig.Poll();

        Assert.Equal("issue-8", Assert.Single(rig.Starter.Started).Driven!.Tasks.Single().Id);
        var waiting = Assert.Single(rig.State.Waiting);
        Assert.Equal((7, "overlap"), (waiting.Issue, waiting.Reason));
        Assert.Contains("#101", waiting.Detail, StringComparison.Ordinal);

        rig.GitHub.OpenPulls.RemoveAll(p => p.Number == 101);
        await rig.Poll();

        Assert.Equal(2, rig.Starter.Started.Count);
        Assert.Empty(rig.State.Waiting);
    }

    [Fact]
    public async Task An_issue_that_declares_no_scope_is_never_held_by_overlap()
    {
        using var rig = new IntakeRig(Settings());
        Scoped(rig, 7, "src/");
        rig.Labeled(8);
        await rig.Poll();

        Assert.Equal(2, rig.Starter.Started.Count);
        Assert.Empty(rig.State.Waiting);
    }

    [Theory]
    [InlineData("src/a.cs", "src/a.cs", true)]
    [InlineData("src", "src/a.cs", true)]
    [InlineData("src/a.cs", "src", true)]
    [InlineData("src/a", "src/ab.cs", false)]
    [InlineData("docs", "src/docs", false)]
    public void Paths_overlap_when_equal_or_one_holds_the_other(string a, string b, bool overlaps) => Assert.Equal(overlaps, ScopeOverlap.Overlaps(a, b));

    [Fact]
    public void A_scope_is_read_from_every_scope_line()
    {
        Assert.Equal(["src/a.cs", "docs", "tests/b.cs"], ScopeOverlap.Declared("Intro\n**Scope:** `./src/a.cs`, docs/**\n- scope: tests/b.cs src/a.cs\nThe scope is wide."));
        Assert.Empty(ScopeOverlap.Declared("No declared paths here."));
    }
}
