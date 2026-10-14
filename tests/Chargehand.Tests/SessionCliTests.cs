using Chargehand.Driven;
using Chargehand.Modules.Oversight.Api;

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

    [Fact]
    public void A_member_a_newer_server_added_is_ignored()
    {
        using var dir = new TempDir();
        var file = dir.Write("task.json", """{"run_id":"run1","goal":"Add a retry","branch":"chargehand/run1","max_turns":80,"max_minutes":45,"no_progress_seconds":600,"max_continuations":2,"nested":{"a":[1]}}""");
        Assert.Equal(new SessionTask("run1", "Add a retry", "chargehand/run1", 80, 45, 600), SessionCli.ReadTask(file));
    }

    [Fact]
    public void The_governor_goes_through_the_task_file_with_snake_case_names_and_an_unknown_member_in_it_is_ignored()
    {
        using var dir = new TempDir();
        var task = new SessionTask("run1", "g", "chargehand/run1", 80, 45, 600, Governor: new SessionGovernor(BudgetUsd: 4m, PriorUsd: 1.25m, SizeClass: "M", RepeatCalls: 5));
        var json = System.Text.Json.JsonSerializer.Serialize(task, SessionCli.Json);
        Assert.Contains("\"governor\":{\"mode\":\"observe\",\"budget_usd\":4,\"prior_usd\":1.25,\"size_class\":\"M\",\"repeat_calls\":5", json);
        var read = SessionCli.ReadTask(dir.Write("task.json", json.Replace("\"repeat_calls\"", "\"later_key\":1,\"repeat_calls\"")));
        Assert.Equal(task.Governor, read.Governor);
        Assert.DoesNotContain("governor", System.Text.Json.JsonSerializer.Serialize(task with { Governor = null }, SessionCli.Json));
    }

    [Theory]
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
