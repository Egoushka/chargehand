namespace Chargehand.Tests;

/// <summary>A squash merge makes the pull request title the commit on main, which release-please reads. The title check
/// runs on its own workflow so that editing the title re-runs it without re-running CI.</summary>
public sealed class PrTitleWorkflowTests
{
    [Fact]
    public void Editing_the_title_reruns_the_title_check()
    {
        var path = Repo.Path(".github", "workflows", "pr-title.yml");
        Assert.True(File.Exists(path), "no .github/workflows/pr-title.yml");
        var text = File.ReadAllText(path);
        Assert.Contains("types: [opened, edited, synchronize, reopened]", text);
        Assert.Contains("scripts/check-commit-msg.sh", text);
        Assert.DoesNotContain("pull request title is a Conventional Commit", File.ReadAllText(Repo.Path(".github", "workflows", "ci.yml")));
    }
}
