using System.Text;
using Chargehand.Contracts;
using Chargehand.Intake;

namespace Chargehand.Tests;

/// <summary>Intake reads the caller's inputs, not only their ids: told just that a diff exists, it asked for the diff the request carried.</summary>
public class IntakeInputsTests
{
    private static async Task<string> IntakePrompt(params CallerInput[] inputs)
    {
        var runtime = new ScriptedRuntime("");
        var request = new RunRequest("request/v1", "Review the change against its goal.", new RequestContext(false, "review"), inputs);
        await new GenerateIntake(runtime, null, PromptBlock.Create("intake/task-spec", "0.0.0", "Intake."), "run-1", needsRepository: false)
            .RunAsync(request, CancellationToken.None);
        return runtime.IntakePrompts.Single();
    }

    [Fact]
    public async Task Each_input_reaches_intake_with_its_id_kind_size_and_text()
    {
        var prompt = await IntakePrompt(new CallerInput("goal", "goal", "Make the README greet."), new CallerInput("tests", "test-output", "exit 0"));

        Assert.Contains("- id \"goal\" (goal, 22 characters): Make the README greet.", prompt, StringComparison.Ordinal);
        Assert.Contains("- id \"tests\" (test-output, 6 characters): exit 0", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_input_at_the_cap_is_not_cut()
    {
        var text = new string('a', GenerateIntake.MaxInputChars);

        var prompt = await IntakePrompt(new CallerInput("diff", "diff", text));

        Assert.Contains($"(diff, {text.Length} characters): {text}", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("cut to", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_input_is_cut_to_its_head_and_the_prompt_says_so()
    {
        var text = new string('a', GenerateIntake.MaxInputChars) + "ZZTAILZZ" + new string('b', 60_000);

        var prompt = await IntakePrompt(new CallerInput("diff", "diff", text), new CallerInput("tests", "test-output", "exit 0"));

        Assert.Contains($"(diff, {text.Length} characters, cut to the first {GenerateIntake.MaxInputChars} here; the worker gets all of it): {new string('a', GenerateIntake.MaxInputChars)}\n",
            prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("ZZTAILZZ", prompt, StringComparison.Ordinal);
        // The next input still follows in full.
        Assert.Contains("- id \"tests\" (test-output, 6 characters): exit 0", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cut_does_not_split_a_surrogate_pair()
    {
        var text = new string('a', GenerateIntake.MaxInputChars - 1) + "\U0001F600" + "tail";

        var prompt = await IntakePrompt(new CallerInput("diff", "diff", text));

        // A lone surrogate makes the JSON body of the generate call throw.
        Assert.Null(Record.Exception(() => new UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(prompt)));
        Assert.DoesNotContain("\U0001F600", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_without_inputs_adds_no_input_section()
    {
        var prompt = await IntakePrompt();

        Assert.DoesNotContain("inputs", prompt.Split("Request:")[1], StringComparison.Ordinal);
    }
}
