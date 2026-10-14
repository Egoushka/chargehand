using System.Text.Json;
using Chargehand.Config;
using Chargehand.Driven;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0045: the governor's deterministic signals and its ladder (warn, then checkpoint; context and spend checkpoint; 100% of the budget stops).</summary>
public class GovernorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static JsonElement Call(string id, string tool, string input) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "assistant",
            message = new { id = $"m-{id}", content = new[] { new { type = "tool_use", id, name = tool, input = JsonDocument.Parse(input).RootElement } } },
        });

    private static JsonElement Bash(string id, string command) => Call(id, "Bash", JsonSerializer.Serialize(new { command }));

    private static JsonElement Result(string id, string output, bool error = false) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "user",
            message = new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = id, content = output, is_error = error } } },
        });

    private static JsonElement Usage(long input, long cacheRead) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "assistant",
            message = new { id = "m-usage", content = Array.Empty<object>(), usage = new { input_tokens = input, output_tokens = 10, cache_read_input_tokens = cacheRead } },
        });

    /// <summary>One call and its result as two stream events, at turn <paramref name="turn"/>; the decisions both produced.</summary>
    private static List<GovernorDecision> Step(Governor g, int turn, JsonElement call, JsonElement result, decimal? usd = null) =>
        [.. g.OnEvent(call, turn, usd, Now), .. g.OnEvent(result, turn, usd, Now)];

    [Fact]
    public void Four_identical_calls_with_identical_output_warn_once_and_three_do_not()
    {
        var g = new Governor(new SessionGovernor());
        for (var i = 1; i <= 3; i++)
            Assert.Empty(Step(g, i, Bash($"c{i}", "dotnet build"), Result($"c{i}", "error CS1002")));
        var d = Assert.Single(Step(g, 4, Bash("c4", "dotnet build"), Result("c4", "error CS1002")));
        Assert.Equal(GovernorAction.Warn, d.Action);
        Assert.Equal("repeated_call", d.Signal);
        Assert.Equal("first_hit", d.Rule);
        Assert.Equal("Bash: dotnet build", d.Values["action"]);
        Assert.Equal(GovernorMode.Observe, d.Mode);
        Assert.False(d.Acted);
    }

    [Fact]
    public void The_same_call_with_a_different_output_each_time_is_not_a_repeat()
    {
        var g = new Governor(new SessionGovernor());
        for (var i = 1; i <= 8; i++)
            Assert.Empty(Step(g, i, Bash($"c{i}", "dotnet test"), Result($"c{i}", $"Passed: {i}")));
    }

    [Fact]
    public void The_same_signal_again_inside_the_rewarn_window_is_quiet_and_after_it_is_a_checkpoint()
    {
        var g = new Governor(new SessionGovernor(RewarnTurns: 10));
        var turn = 0;
        List<GovernorDecision> All(int calls) =>
            [.. Enumerable.Range(0, calls).SelectMany(_ => { turn++; return Step(g, turn, Bash($"c{turn}", "make"), Result($"c{turn}", "same")); })];

        Assert.Equal(GovernorAction.Warn, Assert.Single(All(4)).Action);
        Assert.Empty(All(4));
        Assert.Empty(All(4));
        var checkpoint = Assert.Single(All(4));
        Assert.Equal(GovernorAction.Checkpoint, checkpoint.Action);
        Assert.Equal("same_signal_again", checkpoint.Rule);
        Assert.Equal("4", checkpoint.Values["warned_at_turn"]);
    }

    [Fact]
    public void A_second_signal_while_one_is_warned_is_a_checkpoint_and_clears_the_warnings()
    {
        var g = new Governor(new SessionGovernor());
        for (var i = 1; i <= 2; i++)
            Assert.Empty(Step(g, i, Bash($"a{i}", "npm test"), Result($"a{i}", $"fail {i}", error: true)));
        // The third identical failing action: repeated_failure warns.
        Assert.Equal("repeated_failure", Assert.Single(Step(g, 3, Bash("a3", "npm test"), Result("a3", "fail 3", error: true))).Signal);

        var decisions = Enumerable.Range(1, 4).SelectMany(i => Step(g, 10 + i, Call($"r{i}", "Read", """{"file_path":"a.cs"}"""), Result($"r{i}", "same text"))).ToList();
        var checkpoint = Assert.Single(decisions);
        Assert.Equal(GovernorAction.Checkpoint, checkpoint.Action);
        Assert.Equal("repeated_call", checkpoint.Signal);
        Assert.Equal("second_signal", checkpoint.Rule);
        Assert.Equal("repeated_failure", checkpoint.Values["warned"]);
    }

    [Fact]
    public void Consecutive_failing_calls_of_different_actions_fire_failing_calls_and_a_success_resets_the_count()
    {
        var g = new Governor(new SessionGovernor(FailingCalls: 3, RepeatFailures: 10));
        Step(g, 1, Bash("a", "x1"), Result("a", "no", error: true));
        Step(g, 2, Bash("b", "x2"), Result("b", "no", error: true));
        Step(g, 3, Bash("c", "x3"), Result("c", "ok"));
        Step(g, 4, Bash("d", "x4"), Result("d", "no", error: true));
        Step(g, 5, Bash("e", "x5"), Result("e", "no", error: true));
        var d = Assert.Single(Step(g, 6, Bash("f", "x6"), Result("f", "no", error: true)));
        Assert.Equal("failing_calls", d.Signal);
        Assert.Equal("3", d.Values["in_a_row"]);
    }

    [Fact]
    public void Alternating_between_two_actions_three_times_fires_alternation()
    {
        var g = new Governor(new SessionGovernor());
        var decisions = Enumerable.Range(1, 6)
            .SelectMany(i => Step(g, i, Bash($"c{i}", i % 2 == 1 ? "git stash" : "git stash pop"), Result($"c{i}", $"out {i}"))).ToList();
        Assert.Equal("alternation", Assert.Single(decisions).Signal);
    }

    [Fact]
    public void More_than_the_allowed_commands_that_only_probe_the_container_fire_environment_probing_once()
    {
        var g = new Governor(new SessionGovernor(ProbeCommands: 3));
        string[] probes = ["which dotnet", "dotnet --version", "uname -a && cat /etc/os-release", "ls -la /usr/local/bin"];
        Assert.Empty(g.OnEvent(Bash("p0", "dotnet build && which dotnet"), 1, null, Now));
        var decisions = probes.Select((p, i) => g.OnEvent(Bash($"p{i + 1}", p), i + 2, null, Now)).SelectMany(d => d).ToList();
        var d = Assert.Single(decisions);
        Assert.Equal("environment_probing", d.Signal);
        Assert.Equal("4", d.Values["probe_commands"]);
        Assert.Empty(g.OnEvent(Bash("p9", "which git"), 9, null, Now));
    }

    [Fact]
    public void An_unchanged_diff_for_the_no_growth_turns_fires_and_a_change_rearms_it()
    {
        var g = new Governor(new SessionGovernor(NoGrowthTurns: 10));
        Assert.Empty(g.OnDiff(0, 1, null, Now));
        Assert.Empty(g.OnDiff(0, 10, null, Now));
        var d = Assert.Single(g.OnDiff(0, 11, null, Now));
        Assert.Equal("no_diff_growth", d.Signal);
        Assert.Equal("1", d.Values["unchanged_since_turn"]);
        Assert.Empty(g.OnDiff(42, 12, null, Now));
        Assert.Empty(g.OnDiff(42, 21, null, Now));
    }

    [Fact]
    public void A_command_that_waits_on_purpose_counts_toward_no_repeat_and_pauses_no_growth_while_it_runs()
    {
        var g = new Governor(new SessionGovernor(NoGrowthTurns: 2));
        for (var i = 1; i <= 6; i++)
            Assert.Empty(Step(g, i, Bash($"w{i}", "sleep 30"), Result($"w{i}", "")));
        Assert.Empty(g.OnDiff(5, 1, null, Now));
        Assert.Empty(g.OnEvent(Bash("watch", "gh run watch 123"), 2, null, Now));
        Assert.Empty(g.OnDiff(5, 9, null, Now));
        Assert.Empty(g.OnEvent(Result("watch", "done"), 9, null, Now));
        Assert.Single(g.OnDiff(5, 9, null, Now));
    }

    [Fact]
    public void A_context_above_the_limit_is_one_checkpoint()
    {
        var g = new Governor(new SessionGovernor(ContextTokens: 100_000));
        Assert.Empty(g.OnEvent(Usage(1_000, 90_000), 1, null, Now));
        var d = Assert.Single(g.OnEvent(Usage(2_000, 99_000), 2, null, Now));
        Assert.Equal((GovernorAction.Checkpoint, "context_size"), (d.Action, d.Rule));
        Assert.Equal("101000", d.Values["context_tokens"]);
        Assert.Empty(g.OnEvent(Usage(2_000, 150_000), 3, null, Now));
    }

    [Fact]
    public void Spend_with_earlier_sessions_checkpoints_at_85_percent_and_stops_at_the_budget()
    {
        var g = new Governor(new SessionGovernor(BudgetUsd: 4m, PriorUsd: 1m, SizeClass: "M"));
        Assert.Empty(g.OnEvent(Usage(1, 1), 1, 2.3m, Now));
        var checkpoint = Assert.Single(g.OnEvent(Usage(1, 1), 2, 2.5m, Now));
        Assert.Equal((GovernorAction.Checkpoint, "spend_checkpoint"), (checkpoint.Action, checkpoint.Rule));
        Assert.Equal((3.5m, 4m, "M"), (checkpoint.SpendUsd, checkpoint.BudgetUsd, checkpoint.SizeClass));
        Assert.Empty(g.OnEvent(Usage(1, 1), 3, 2.9m, Now));
        var stop = Assert.Single(g.OnEvent(Usage(1, 1), 4, 3m, Now));
        Assert.Equal((GovernorAction.Stop, "spend_stop"), (stop.Action, stop.Rule));
        Assert.Empty(g.OnEvent(Usage(1, 1), 5, 9m, Now));
    }

    [Fact]
    public void Every_decision_carries_where_the_budget_came_from()
    {
        var source = new BudgetSource("history", 23, 80, "repository");
        var g = new Governor(new SessionGovernor(BudgetUsd: 4m, SizeClass: "M", BudgetSource: source));
        var checkpoint = Assert.Single(g.OnEvent(Usage(1, 1), 1, 3.5m, Now));
        Assert.Equal(source, checkpoint.BudgetSource);
        Assert.Equal(source, Assert.Single(g.OnEvent(Usage(1, 1), 2, 4.1m, Now)).BudgetSource);
        Assert.Null(Assert.Single(new Governor(new SessionGovernor(BudgetUsd: 4m)).OnEvent(Usage(1, 1), 1, 5m, Now), d => d.Action == GovernorAction.Stop).BudgetSource);
    }

    [Fact]
    public void Without_a_budget_or_a_priced_spend_there_is_no_spend_rule()
    {
        Assert.Empty(new Governor(new SessionGovernor()).OnEvent(Usage(1, 1), 1, 100m, Now));
        Assert.Empty(new Governor(new SessionGovernor(BudgetUsd: 1m)).OnEvent(Usage(1, 1), 1, null, Now));
    }

    [Fact]
    public void A_decision_serializes_with_snake_case_names_and_values()
    {
        var d = new GovernorDecision(Now, 4, GovernorMode.Observe, GovernorAction.Checkpoint, "spend", "spend_checkpoint", new Dictionary<string, string> { ["share"] = "0.85" }, 3.4m, 4m, "M");
        var json = JsonDocument.Parse(JsonSerializer.Serialize(d, Snake)).RootElement;
        Assert.Equal("observe", json.GetProperty("mode").GetString());
        Assert.Equal("checkpoint", json.GetProperty("action").GetString());
        Assert.Equal(3.4m, json.GetProperty("spend_usd").GetDecimal());
        Assert.False(json.GetProperty("acted").GetBoolean());
    }
}
