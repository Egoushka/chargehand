namespace Chargehand.Tests;

/// <summary>The run skill hands a prompt to a driven batch and only observes it: the names, the request it builds and the tools it may use are pinned here.</summary>
public class RunSkillTests
{
    private static readonly string Skill = File.ReadAllText(Repo.Path("plugins", "chargehand", "skills", "run", "SKILL.md")).ReplaceLineEndings("\n");

    private static string FrontMatter(string key) =>
        Skill.Split('\n').Skip(1).TakeWhile(l => l != "---").Single(l => l.StartsWith(key + ":", StringComparison.Ordinal))[(key.Length + 1)..].Trim();

    [Fact]
    public void Front_matter_names_the_skill_and_takes_a_prompt()
    {
        Assert.StartsWith("---\nname: run\n", Skill, StringComparison.Ordinal);
        Assert.Contains("argument-hint: <prompt>", Skill, StringComparison.Ordinal);
        Assert.Contains("$ARGUMENTS", Skill, StringComparison.Ordinal);
    }

    [Fact]
    public void The_session_may_start_the_batch_watch_it_and_read_git_state_and_nothing_else()
    {
        var tools = System.Text.RegularExpressions.Regex.Matches(FrontMatter("allowed-tools"), @"Bash\([^)]*\)|\S+").Select(m => m.Value);
        Assert.Equal(
            ["Bash(chargehand cancel:*)", "Bash(chargehand show:*)", "Bash(chargehand watch:*)", "Bash(git rev-parse:*)", "Bash(git status:*)", "Monitor", "mcp__chargehand__orchestrate"],
            tools.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("\"driven\"")]                          // the preset
    [InlineData("max_parallel")]
    [InlineData("Monitor")]
    [InlineData("chargehand watch")]
    [InlineData("chargehand cancel")]
    [InlineData("is still running")]                    // the tool error that carries the run id
    [InlineData("never merges")]
    [InlineData("Do not do the task yourself")]
    public void Keeps_the_names_and_limits_the_skill_depends_on(string text) => Assert.Contains(text, Skill, StringComparison.Ordinal);

    [Fact]
    public void Driven_sessions_that_are_off_are_reported_not_switched_on()
    {
        Assert.Contains("driven sessions are off", Skill, StringComparison.Ordinal);
        Assert.Contains("never edit a profile", Skill, StringComparison.Ordinal);
    }

    [Fact]
    public void Names_every_exit_code_of_watch()
    {
        foreach (var code in new[] { "exit 0", "exit 1", "exit 2", "exit 3" })
            Assert.Contains(code, Skill, StringComparison.Ordinal);
    }
}
