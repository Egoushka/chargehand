using Chargehand.Driven;

namespace Chargehand.Tests;

public class SessionCliTests
{
    [Fact]
    public void A_task_file_becomes_a_session_task()
    {
        using var dir = new TempDir();
        var file = dir.Write("task.json", """{"run_id":"run1","goal":"Add a retry","branch":"chargehand/run1","max_turns":80,"max_minutes":45,"no_progress_seconds":600,"max_tokens":2000000,"model":"claude-sonnet-5-5","max_usd":3.5}""");
        var task = SessionCli.ReadTask(file);
        Assert.Equal(new SessionTask("run1", "Add a retry", "chargehand/run1", 80, 45, 600, 2_000_000, "claude-sonnet-5-5", 3.5m), task);
    }

    [Theory]
    [InlineData("""{"run_id":"run1","goal":"g","branch":"chargehand/run1","max_turns":80,"max_minutes":45,"no_progress_seconds":600,"extra":1}""")]   // unknown field
    [InlineData("""{"run_id":"run1","goal":"","branch":"chargehand/run1","max_turns":80,"max_minutes":45,"no_progress_seconds":600}""")]             // empty goal
    [InlineData("""{"run_id":"run1","goal":"g","branch":"main","max_turns":80,"max_minutes":45,"no_progress_seconds":600}""")]                     // not a chargehand/ branch
    [InlineData("""{"run_id":"run1","goal":"g","branch":"chargehand/run1","max_turns":0,"max_minutes":45,"no_progress_seconds":600}""")]            // no turns
    [InlineData("not json")]
    public void A_bad_task_file_is_refused(string json)
    {
        using var dir = new TempDir();
        Assert.Throws<InvalidDataException>(() => SessionCli.ReadTask(dir.Write("task.json", json)));
    }

    [Fact]
    public void The_default_prompt_says_no_person_is_there_and_names_the_report_and_the_questions_marker()
    {
        Assert.Contains("no person", SessionCli.DefaultPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NEEDS_INPUT:", SessionCli.DefaultPrompt);
        Assert.Contains("```json", SessionCli.DefaultPrompt);
        Assert.Contains("CHARGEHAND_BRANCH", SessionCli.DefaultPrompt);
    }
}
