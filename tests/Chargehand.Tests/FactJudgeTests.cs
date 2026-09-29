using Chargehand.Evals;

namespace Chargehand.Tests;

public class FactJudgeTests
{
    [Theory]
    [InlineData("""{"stated": [1, 3, 3], "repeated": [2]}""", 2, 1)]
    [InlineData("Here it is: {\"stated\": [], \"repeated\": []} done", 0, 0)]
    [InlineData("""{"stated": ["F1", "2"], "repeated": ["W1"]}""", 2, 1)]
    public void A_verdict_counts_distinct_facts_and_statements(string text, int stated, int repeated)
    {
        Assert.True(FactJudge.Parse(text, facts: 3, wrong: 2, out var check, out _));
        Assert.Equal(new FactCheck(stated, 3, repeated), check);
    }

    [Theory]
    [InlineData("no object here")]
    [InlineData("""{"stated": [4], "repeated": []}""")] // there are only 3 facts
    [InlineData("""{"stated": [1]}""")]
    [InlineData("""{"stated": ["one"], "repeated": []}""")]
    [InlineData("""{"stated": ["F9"], "repeated": []}""")]
    public void A_malformed_verdict_is_refused(string text) =>
        Assert.False(FactJudge.Parse(text, facts: 3, wrong: 2, out _, out _));

    [Fact]
    public async Task An_invalid_verdict_gets_one_retry_then_fails()
    {
        var runtime = new ScriptedRuntime(Runs.WorkerReply, "nonsense", """{"stated": [1], "repeated": []}""");
        var result = Runs.Completed();

        var check = await FactJudge.JudgeAsync(runtime, new Runtime.ModelRef("p", "small"), result, ["a"], [], CancellationToken.None);

        Assert.Equal(new FactCheck(1, 1, 0), check);
        Assert.Contains("previous output was invalid", runtime.IntakePrompts.Last(), StringComparison.Ordinal);
        var failing = new ScriptedRuntime(Runs.WorkerReply, "nonsense", "still nonsense", "and again");
        await Assert.ThrowsAsync<InvalidOperationException>(() => FactJudge.JudgeAsync(failing, new Runtime.ModelRef("p", "small"), result, ["a"], [], CancellationToken.None));
    }
}
