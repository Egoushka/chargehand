namespace Chargehand.Tests;

/// <summary>Goal 0.5: the change skill keeps the names and limits the spec fixes.</summary>
public class ChangeSkillTests
{
    private static readonly string Skill = File.ReadAllText(Repo.Path("plugins", "chargehand", "skills", "change", "SKILL.md"));

    [Fact]
    public void Front_matter_names_the_skill_and_takes_a_goal()
    {
        Assert.StartsWith("---\nname: change\n", Skill.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("argument-hint:", Skill, StringComparison.Ordinal);
        Assert.Contains("$ARGUMENTS", Skill, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("change/<slug>")]
    [InlineData(".chargehand/reports/<slug>.md")]
    [InlineData("docs: add /change report")]
    [InlineData("preset \"review\"")]
    [InlineData("at most 2 fix rounds")]
    [InlineData("60000 characters")]
    public void Keeps_the_spec_names_and_limits(string text) => Assert.Contains(text, Skill, StringComparison.Ordinal);

    [Fact]
    public void Report_template_has_every_section()
    {
        var template = File.ReadAllText(Repo.Path("plugins", "chargehand", "skills", "change", "report-template.md"));
        foreach (var h in new[] { "## Goal", "## Research", "## Change", "## Tests", "## Review rounds", "## Open items", "## Runs" })
            Assert.Contains(h, template, StringComparison.Ordinal);
    }

    private static readonly string[] SkillSteps = ["## 1. Preflight", "## 2. Research", "## 3. Branch", "## 4. Write", "## 5. Test", "## 6. Commit", "## 7. Review", "## 8. Fix loop", "## 9. Report", "## 10. Hand back"];

    [Fact]
    public void Every_step_is_still_there_in_order_and_the_driven_section_comes_before_them()
    {
        var text = Skill.ReplaceLineEndings("\n");
        var at = -1;
        foreach (var step in SkillSteps)
        {
            var next = text.IndexOf(step, StringComparison.Ordinal);
            Assert.True(next > at, $"{step} is missing or out of order");
            at = next;
        }
        Assert.True(text.IndexOf("## Driven mode", StringComparison.Ordinal) is > 0 and var driven && driven < text.IndexOf(SkillSteps[0], StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("CHARGEHAND_DRIVEN")]
    [InlineData("CHARGEHAND_BRANCH")]
    [InlineData("NEEDS_INPUT:")]
    [InlineData("do not push")]
    [InlineData("```json")]
    public void The_driven_section_names_what_a_headless_session_does_differently(string text)
    {
        var section = Skill[Skill.IndexOf("## Driven mode", StringComparison.Ordinal)..Skill.IndexOf(SkillSteps[0], StringComparison.Ordinal)];
        Assert.Contains(text, section, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_driven_section_changes_only_steps_that_need_a_person_and_says_which()
    {
        var section = Skill[Skill.IndexOf("## Driven mode", StringComparison.Ordinal)..Skill.IndexOf(SkillSteps[0], StringComparison.Ordinal)];
        foreach (var step in new[] { "Step 1:", "Steps 2, 7 and 8:", "Step 3:", "Step 9:", "Step 10:" })
            Assert.Contains(step, section, StringComparison.Ordinal);
        foreach (var untouched in new[] { "Step 4", "Step 5", "Step 6", "Steps 4", "Steps 5", "Steps 6" })
            Assert.DoesNotContain(untouched, section, StringComparison.Ordinal);
    }

    [Fact]
    public void The_drivers_prompt_and_the_skill_agree_on_the_words_they_share()
    {
        var prompt = Chargehand.Driven.SessionCli.DefaultPrompt;
        foreach (var shared in new[] { "NEEDS_INPUT:", "CHARGEHAND_BRANCH", "```json" })
        {
            Assert.Contains(shared, prompt, StringComparison.Ordinal);
            Assert.Contains(shared, Skill, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_person_facing_flow_is_untouched_by_the_driven_section()
    {
        // The whole flow for a person still says what it said: the driven section only adds a mode.
        foreach (var text in new[] { "Commit or stash your changes first", "ask the user all of them in one message", "Tell the user: the branch name" })
            Assert.Contains(text, Skill.Replace("\r\n", "\n").Replace("\n", " "), StringComparison.Ordinal);
    }
}
