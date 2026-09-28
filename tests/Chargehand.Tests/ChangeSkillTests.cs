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
}
