using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Intake;

namespace Chargehand.Tests;

/// <summary>
/// Review runs got "diffs" that were a placeholder word, a file path on the caller's machine, or "see attached diff", and one completed with
/// findings on 11 characters. A diff input that is not a unified patch stops at intake with needs_input, before any model call.
/// </summary>
public class IntakeDiffTests
{
    private static async Task<(IntakeOutcome Outcome, ScriptedRuntime Runtime)> Intake(string kind, string text)
    {
        var runtime = new ScriptedRuntime("");
        var request = new RunRequest("request/v1", "Review the change against its goal.", new RequestContext(false, "review"),
            [new CallerInput("goal", "goal", "Make the README greet."), new CallerInput("diff", kind, text)]);
        var outcome = await new GenerateIntake(runtime, null, PromptBlock.Create("intake/task-spec", "0.0.0", "Intake."), "run-1", needsRepository: false)
            .RunAsync(request, CancellationToken.None);
        return (outcome, runtime);
    }

    [Theory]
    [InlineData("placeholder")]
    [InlineData("DIFF_PLACEHOLDER")]
    [InlineData("FILE:/tmp/diff_final.txt")]
    [InlineData("See attached diff (14100 chars).")]
    [InlineData("git diff c32a235..HEAD")]
    [InlineData("")]
    public async Task A_diff_that_is_not_a_patch_needs_input_before_any_model_call(string text)
    {
        var (outcome, runtime) = await Intake("diff", text);

        Assert.Null(outcome.Spec);
        Assert.Contains("not a unified patch", outcome.NeedsInput, StringComparison.Ordinal);
        Assert.Contains("\"diff\"", outcome.NeedsInput, StringComparison.Ordinal);
        Assert.Empty(runtime.IntakePrompts);
    }

    [Fact]
    public async Task A_patch_kind_is_checked_too_and_other_kinds_are_not()
    {
        Assert.Null((await Intake("patch", "see attached")).Outcome.Spec);
        Assert.NotNull((await Intake("note", "see attached")).Outcome.Spec);
    }

    [Theory]
    [InlineData("--- a/README.md\n+++ b/README.md\n@@ -1 +1 @@\n-hello\n+hello world\n")]
    [InlineData("diff --git a/old.txt b/new.txt\nsimilarity index 100%\nrename from old.txt\nrename to new.txt\n")]
    [InlineData("diff --git a/logo.png b/logo.png\nBinary files a/logo.png and b/logo.png differ\n")]
    [InlineData("--- a/x\n+++ b/x\n@@ -0,0 +1,2 @@\n+x\n[diff truncated at 60000 characters]\n")]
    public async Task A_unified_patch_goes_on_to_the_model(string patch)
    {
        Assert.True(GenerateIntake.IsUnifiedPatch(patch));
        var (outcome, runtime) = await Intake("diff", patch);

        Assert.NotNull(outcome.Spec);
        Assert.Single(runtime.IntakePrompts);
    }

    [Fact]
    public void A_hunk_with_no_file_header_is_not_a_patch()
    {
        Assert.False(GenerateIntake.IsUnifiedPatch("@@ -1 +1 @@\n-a\n+b\n"));
        Assert.False(GenerateIntake.IsUnifiedPatch("--- a/x\n+++ b/x\n"));
    }

    [Theory]
    [InlineData("cheap", TaskAction.Ask, TaskAction.Ask)]
    [InlineData("thorough", TaskAction.Split, TaskAction.Split)]
    [InlineData("draft", TaskAction.Deny, TaskAction.Deny)]
    [InlineData("code", TaskAction.Ask, TaskAction.Ask)]
    [InlineData("default", TaskAction.Ask, TaskAction.Answer)]
    [InlineData("review", TaskAction.Ask, TaskAction.Answer)]
    [InlineData("default", TaskAction.Split, TaskAction.Answer)]
    [InlineData("draft", TaskAction.Improve, TaskAction.Answer)]
    public void Intakes_choice_runs_when_the_preset_allows_it_and_answer_runs_otherwise(string preset, TaskAction chosen, TaskAction executed) =>
        Assert.Equal(executed, Preset.Load(Repo.Path("presets"), preset).Executes(chosen));
}
