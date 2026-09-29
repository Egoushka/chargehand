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

    /// <summary>Inputs whose texts add up to <paramref name="total"/>, each within the per-input cap; input n is filled with the letter 'a' + n.</summary>
    private static CallerInput[] Filling(int total)
    {
        var inputs = new List<CallerInput>();
        for (var left = total; left > 0; left -= GenerateIntake.MaxInputChars)
            inputs.Add(new CallerInput($"i{inputs.Count}", "diff", new string((char)('a' + inputs.Count), Math.Min(GenerateIntake.MaxInputChars, left))));
        return [.. inputs];
    }

    private static void AssertWhole(string prompt, IEnumerable<CallerInput> inputs)
    {
        foreach (var input in inputs)
            Assert.Contains($"- id \"{input.Id}\" ({input.Kind}, {input.Text.Length} characters): {input.Text}\n", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inputs_share_one_budget_the_crossing_input_is_cut_and_later_ones_lose_their_text()
    {
        const int room = 300;
        var early = Filling(GenerateIntake.MaxTotalInputChars - room);
        var crossing = new CallerInput("crossing", "diff", new string('x', 1000));
        var late = new CallerInput("late", "test-output", new string('y', 1500));
        var beyond = new CallerInput("beyond", "note", new string('z', 40));

        var prompt = await IntakePrompt([.. early, crossing, late, beyond]);

        AssertWhole(prompt, early);
        Assert.Contains($"- id \"crossing\" (diff, 1000 characters, cut to the first {room} here; the worker gets all of it): {new string('x', room)}\n", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', room + 1), prompt, StringComparison.Ordinal);
        Assert.Contains("- id \"late\" (test-output, 1500 characters, text left out here; the worker gets all of it)\n", prompt, StringComparison.Ordinal);
        Assert.Contains("- id \"beyond\" (note, 40 characters, text left out here; the worker gets all of it)", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("yyy", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("zzz", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_input_past_the_budget_is_listed_without_text_and_is_never_shown_as_empty()
    {
        var prompt = await IntakePrompt([.. Filling(GenerateIntake.MaxTotalInputChars), new CallerInput("late", "diff", "one line"), new CallerInput("blank", "note", "")]);

        Assert.Contains("- id \"late\" (diff, 8 characters, text left out here; the worker gets all of it)\n", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("one line", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("(diff, 0 characters", prompt, StringComparison.Ordinal);
        // An input that really is empty is still reported as empty: only text that exists is left out.
        Assert.Contains("- id \"blank\" (note, 0 characters): \n", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inputs_that_exactly_fill_the_budget_are_whole_and_the_next_one_is_left_out()
    {
        CallerInput[] exact = [.. Filling(GenerateIntake.MaxTotalInputChars - 2), new CallerInput("last", "diff", "zz")];

        var prompt = await IntakePrompt([.. exact, new CallerInput("after", "diff", "q")]);

        AssertWhole(prompt, exact);
        Assert.DoesNotContain("cut to", prompt, StringComparison.Ordinal);
        Assert.Contains("- id \"after\" (diff, 1 characters, text left out here; the worker gets all of it)\n", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_character_over_the_budget_cuts_the_last_input_to_what_is_left()
    {
        var prompt = await IntakePrompt([.. Filling(GenerateIntake.MaxTotalInputChars - 1), new CallerInput("last", "diff", "zz")]);

        Assert.Contains("- id \"last\" (diff, 2 characters, cut to the first 1 here; the worker gets all of it): z\n", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_budget_cut_does_not_split_a_surrogate_pair()
    {
        // Two characters of room: "a" fits, and the high surrogate that follows would be left as half a pair.
        var cut = await IntakePrompt([.. Filling(GenerateIntake.MaxTotalInputChars - 2), new CallerInput("last", "diff", "a\U0001F600tail")]);
        // One character of room, and the input starts with a pair: nothing of it can be shown.
        var none = await IntakePrompt([.. Filling(GenerateIntake.MaxTotalInputChars - 1), new CallerInput("last", "diff", "\U0001F600tail")]);

        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        Assert.Null(Record.Exception(() => strict.GetBytes(cut)));
        Assert.Null(Record.Exception(() => strict.GetBytes(none)));
        Assert.Contains("- id \"last\" (diff, 7 characters, cut to the first 1 here; the worker gets all of it): a\n", cut, StringComparison.Ordinal);
        Assert.Contains("- id \"last\" (diff, 6 characters, text left out here; the worker gets all of it)\n", none, StringComparison.Ordinal);
        Assert.DoesNotContain("\U0001F600", cut + none, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_without_inputs_adds_no_input_section()
    {
        var prompt = await IntakePrompt();

        Assert.DoesNotContain("inputs", prompt.Split("Request:")[1], StringComparison.Ordinal);
    }
}
