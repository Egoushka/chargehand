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
    [InlineData("CHARGEHAND_REPOSITORY_PATH")]
    [InlineData("CHARGEHAND_BASE_COMMIT")]
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
        foreach (var step in new[] { "Step 1:", "Steps 2 and 7:", "Steps 2 and 8:", "Step 7:", "Step 3:", "Step 9:", "Step 10:" })
            Assert.Contains(step, section, StringComparison.Ordinal);
        foreach (var untouched in new[] { "Step 4", "Step 5", "Step 6", "Steps 4", "Steps 5", "Steps 6" })
            Assert.DoesNotContain(untouched, section, StringComparison.Ordinal);
    }

    [Fact]
    public void A_driven_review_reads_the_change_from_the_diff_and_its_questions_do_not_stop_the_session()
    {
        // Seen twice: the review checkout is the base commit, the worker could not see the new code, asked, and the session stopped for the owner.
        var review = File.ReadAllText(Repo.Path("prompts", "preset", "review.md"));
        Assert.Contains("may be the base commit", review, StringComparison.Ordinal);
        Assert.Contains("Never return needs_input because", review, StringComparison.Ordinal);
        var section = Skill[Skill.IndexOf("## Driven mode", StringComparison.Ordinal)..Skill.IndexOf(SkillSteps[0], StringComparison.Ordinal)];
        Assert.Contains("does not stop the session", section, StringComparison.Ordinal);
        Assert.Contains("does not stop you", Chargehand.Modules.Sessions.Api.SessionCli.DefaultPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_driven_session_reads_a_running_call_with_result_and_goes_on_without_research_that_fails()
    {
        // Seen in production: research returned only a run id, result was denied, and the session stopped to ask the owner for the permission (CHARGEHAND-246).
        static string Flat(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
        var section = Flat(Skill[Skill.IndexOf("## Driven mode", StringComparison.Ordinal)..Skill.IndexOf(SkillSteps[0], StringComparison.Ordinal)]);
        var prompt = Flat(Chargehand.Modules.Sessions.Api.SessionCli.DefaultPrompt);
        foreach (var text in new[] { "call the `result` tool", "continue without research", "never ask for a tool permission" })
        {
            Assert.Contains(text, section, StringComparison.Ordinal);
            Assert.Contains(text, prompt, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("stays unreachable, `NEEDS_INPUT:`", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_drivers_prompt_and_the_skill_agree_on_the_words_they_share()
    {
        var prompt = Chargehand.Modules.Sessions.Api.SessionCli.DefaultPrompt;
        foreach (var shared in new[] { "NEEDS_INPUT:", "CHARGEHAND_BRANCH", "CHARGEHAND_REPOSITORY_PATH", "CHARGEHAND_BASE_COMMIT", "```json" })
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

    [Fact]
    public void The_local_flag_names_the_local_server_and_the_request_field_on_both_calls()
    {
        var text = Skill.Replace("\r\n", "\n").Replace("\n", " ");

        Assert.Contains("[--local]", text, StringComparison.Ordinal);
        Assert.Contains("MCP server named `chargehand-local`", text, StringComparison.Ordinal);
        Assert.Contains("`context.delivery` = \"local\" on every call in steps 2 and 7", text, StringComparison.Ordinal);
    }
}
