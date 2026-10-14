using Chargehand.Bench;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Tests;

/// <summary>What the benchmark reads out of a batch's events, pinned to the lines the server really writes.</summary>
public class BenchTelemetryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);

    private static RunStatus Event(RunEventKind kind, string? task, int seconds, string? detail = null) =>
        RunStatus.Of("batch-1", RunState.Running, kind) with { TaskId = task, At = T0.AddSeconds(seconds), Detail = detail };

    private static RunStatus Progress(string task, int seconds, long tokens, int turns, long cacheRead = 0, long cacheWrite = 0, decimal? usd = null, bool partial = false) =>
        Event(RunEventKind.SessionProgress, task, seconds) with { Tokens = tokens, Turns = turns, CacheRead = cacheRead, CacheWrite = cacheWrite, UsdEstimate = usd, UsdEstimatePartial = partial };

    [Fact]
    public void The_last_tally_wins_and_the_task_finished_event_has_the_final_tokens()
    {
        var t = TaskTelemetry.Summarize([
            Event(RunEventKind.ContainerStarted, "t1", 5),
            Progress("t1", 10, 1000, 4, 50_000, 2_000, 0.05m),
            Progress("t1", 40, 3000, 12, 150_000, 5_000, 0.20m),
            Progress("t2", 41, 9999, 99),
            Event(RunEventKind.TaskFinished, "t1", 65) with { Tokens = 3300, Branch = "chargehand/run-1", PrUrl = "https://example.test/pull/1" },
        ], "t1");
        Assert.Equal(3300, t.Tokens);
        Assert.Equal(12, t.Turns);
        Assert.Equal(150_000, t.CacheRead);
        Assert.Equal(5_000, t.CacheWrite);
        Assert.Equal(0.20m, t.UsdEstimate);
        Assert.Equal(60, t.WallSeconds);
        Assert.Equal("chargehand/run-1", t.Branch);
        Assert.Equal("https://example.test/pull/1", t.PrUrl);
    }

    [Fact]
    public void A_wait_for_memory_line_is_not_a_tally()
    {
        var t = TaskTelemetry.Summarize([
            Event(RunEventKind.SessionProgress, "t1", 1, "task t1: waiting for memory (2210 MiB available, 1024 reserved, 4096 needed)"),
            Progress("t1", 20, 500, 3),
        ], "t1");
        Assert.Equal(500, t.Tokens);
    }

    [Fact]
    public void Continuations_and_questions_are_counted_from_their_events()
    {
        var t = TaskTelemetry.Summarize([
            Event(RunEventKind.ContainerStarted, "t1", 0, "session container started"),
            Event(RunEventKind.ContainerStarted, "t1", 600, "session container started (continuation 1 of 2, previous stopped by turn_limit)"),
            Event(RunEventKind.ContainerStarted, "t1", 1200, "session container started (continuation 2 of 2, previous stopped by checkpoint_stuck)"),
            Event(RunEventKind.Question, "t1", 1300, "task t1: the session asked:\n- which database?"),
            Event(RunEventKind.ContainerStarted, "t2", 0),
        ], "t1");
        Assert.Equal(2, t.Continuations);
        Assert.True(t.AskedQuestion);
        Assert.False(TaskTelemetry.Summarize([Event(RunEventKind.ContainerStarted, "t2", 0)], "t2").AskedQuestion);
    }

    [Fact]
    public void A_task_the_events_do_not_mention_is_empty()
    {
        Assert.Equal(TaskTelemetry.Empty, TaskTelemetry.Summarize([Progress("t9", 1, 5, 1)], "t1"));
        Assert.Equal(TaskTelemetry.Empty, TaskTelemetry.Summarize([], "t1"));
    }

    [Fact]
    public void Two_rounds_of_a_task_add_up_and_the_later_branch_stands()
    {
        var first = new TaskTelemetry(1000, 100, 10, 0.10m, false, 5, 1, true, [new GovernorDecisionRecord("warn", "s", "r", 3, false)], 60, "chargehand/a", null);
        var second = new TaskTelemetry(500, 50, 5, null, true, 3, 0, false, [], 30, null, "https://example.test/pull/2");
        var sum = TaskTelemetry.Merge(first, second);
        Assert.Equal((1500, 150, 15), (sum.Tokens, sum.CacheRead, sum.CacheWrite));
        Assert.Equal(0.10m, sum.UsdEstimate);
        Assert.True(sum.UsdEstimatePartial);
        Assert.Equal((8, 1, 90), (sum.Turns, sum.Continuations, sum.WallSeconds));
        Assert.True(sum.AskedQuestion);
        Assert.Single(sum.Governor);
        Assert.Equal(("chargehand/a", "https://example.test/pull/2"), (sum.Branch, sum.PrUrl));
        Assert.Null(TaskTelemetry.Merge(TaskTelemetry.Empty, TaskTelemetry.Empty).UsdEstimate);
    }

    [Fact]
    public void The_result_of_the_task_raises_what_the_events_missed_and_never_lowers_it()
    {
        var t = new TaskTelemetry(1000, 5000, 100, null, false, 1, 0, false, [], 1, null, null);
        var raised = t.WithResult(new Usage(900, 400, 9000, 0, null));
        Assert.Equal((1300, 9000, 100), (raised.Tokens, raised.CacheRead, raised.CacheWrite));
        var kept = t.WithResult(new Usage(100, 100, 100, 0, null));
        Assert.Equal((1000, 5000), (kept.Tokens, kept.CacheRead));
        Assert.Same(t, t.WithResult(null));
    }

    [Theory]
    [InlineData(GovernorMode.Observe, false, GovernorAction.Warn, "repeated_call", "repeated call", 12, null, null)]
    [InlineData(GovernorMode.Observe, false, GovernorAction.Checkpoint, "spend", "budget share", 40, 3.4, 4.0)]
    [InlineData(GovernorMode.Enforce, true, GovernorAction.Stop, "spend", "budget spent", 60, 4.0, 4.0)]
    [InlineData(GovernorMode.Enforce, false, GovernorAction.Warn, "no_diff_growth", "no diff growth", 41, null, null)]
    public void A_governor_line_the_server_writes_is_read_back(GovernorMode mode, bool acted, GovernorAction action, string signal, string rule, int turn, double? spend, double? budget)
    {
        var decision = new GovernorDecision(T0, turn, mode, action, signal, rule.Replace(' ', '_'), new Dictionary<string, string>(), (decimal?)spend, (decimal?)budget, "M", acted);
        var line = TaskRunner.Describe(decision);
        var read = TaskTelemetry.ParseGovernor(line);
        Assert.NotNull(read);
        Assert.Equal(action.ToString().ToLowerInvariant(), read.Action);
        Assert.Equal((signal, rule.Replace(' ', '_'), turn, acted), (read.Signal, read.Rule, read.Turn, read.Acted));
        Assert.Equal((decimal?)spend, read.SpendUsd);
        Assert.Equal((decimal?)budget, read.BudgetUsd);
        Assert.Equal(action is GovernorAction.Checkpoint or GovernorAction.Stop, read.WouldEnd);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("task t1: 4 tokens, 1 turns, stage write")]
    [InlineData("governor observe: sneeze on repeated_call (x) at turn 3")]
    public void Anything_else_is_not_a_decision(string? line) => Assert.Null(TaskTelemetry.ParseGovernor(line));

    [Fact]
    public void Governor_events_of_a_task_become_its_decisions()
    {
        var t = TaskTelemetry.Summarize([
            Event(RunEventKind.Governor, "t1", 50, "governor observe: warn on repeated_call (repeated call) at turn 12"),
            Event(RunEventKind.Governor, "t1", 51, "governor observe: checkpoint on repeated_call (repeated call) at turn 25"),
            Event(RunEventKind.Governor, "t2", 52, "governor observe: stop on spend (budget spent) at turn 3, spend 4.00 of 4.00"),
            Event(RunEventKind.Governor, "t1", 53, "not a decision"),
        ], "t1");
        Assert.Equal(["warn", "checkpoint"], t.Governor.Select(d => d.Action));
    }

    [Theory]
    [InlineData("change", "completed", null, true, Outcomes.Passed)]
    [InlineData("change", "completed", null, false, Outcomes.Failed)]
    [InlineData("change", "failed", "session_stalled", true, Outcomes.Failed)]
    [InlineData("question", "needs_input", null, null, Outcomes.Failed)]
    [InlineData("question", "completed", null, true, Outcomes.Passed)]
    [InlineData("impossible", "failed", "verification_failed", null, Outcomes.HonestStop)]
    [InlineData("impossible", "needs_input", null, null, Outcomes.HonestStop)]
    [InlineData("impossible", "failed", "invalid_result", true, Outcomes.HonestStop)]
    [InlineData("impossible", "completed", null, true, Outcomes.FalseDone)]
    [InlineData("impossible", "failed", "verification_failed", false, Outcomes.FalseDone)]
    [InlineData("impossible", "failed", "rate_limited", null, Outcomes.Aborted)]
    [InlineData("impossible", "failed", "session_stalled", null, Outcomes.Aborted)]
    [InlineData("impossible", "not_started", "cancelled", null, Outcomes.Aborted)]
    [InlineData("impossible", "not_run", null, null, Outcomes.NotRun)]
    [InlineData("change", "not_run", null, null, Outcomes.NotRun)]
    public void An_outcome_follows_from_the_kind_the_state_and_the_check(string kind, string state, string? error, bool? accepted, string expected)
    {
        AcceptanceResult? acceptance = accepted is { } a ? new AcceptanceResult(a, a ? 0 : 1, false, 1, "") : null;
        Assert.Equal(expected, Outcomes.Classify(kind, state, error, acceptance));
    }
}
