using System.Globalization;
using Chargehand.Contracts;
using Chargehand.Modules.Batches.Core.Scheduling;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Tests;

/// <summary>ADR 0039 and ADR 0060 rule 2: the scheduler's decisions as a table, with no runner, no clock and no file system. A row folds its events from the start and checks what
/// the last one asks the shell to do.</summary>
public class BatchMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double minutes) => T0.AddMinutes(minutes);

    private static ResolvedTask[] Tasks(int n) => [.. Enumerable.Range(1, n).Select(i => new ResolvedTask($"t{i}", null, $"goal {i}"))];

    private static ResolvedTask Dep(string id, params string[] dependsOn) => new(id, null, $"goal {id}", DependsOn: dependsOn.Length == 0 ? null : dependsOn);

    private static SchedulePlan Plan(int n = 3, int parallel = 2, long? tokens = null, decimal? usd = null, long perTask = 1000, decimal? perTaskUsd = null, bool admission = false) =>
        Plan(Tasks(n), parallel, tokens, usd, perTask, perTaskUsd, admission);

    private static SchedulePlan Plan(ResolvedTask[] tasks, int parallel = 2, long? tokens = null, decimal? usd = null, long perTask = 1000, decimal? perTaskUsd = null, bool admission = false) =>
        new([.. tasks], parallel, tokens, usd, new TaskLimits(perTask, perTaskUsd), TimeSpan.FromMinutes(1), admission ? TimeSpan.FromSeconds(30) : null);

    private static Tick Tick(double minute = 0) => new(At(minute));

    private static Admitted Admit(string id, double minute = 0, string? runner = null, string? forced = null) => new(id, runner, forced, At(minute));

    private static Reported Report(string id, long tokens, decimal usd = 0) => new(id, tokens, usd);

    private static Ended End(string id, TaskState state = TaskState.Completed, long tokens = 100, decimal usd = 0, ErrorCode? error = null, DateTimeOffset? retryAfter = null,
        double minute = 0) =>
        new(id, new TaskOutcome(id, state, $"run-{id}", null, error, usd, tokens, state == TaskState.Completed ? $"chargehand/run-{id}" : null,
            state == TaskState.Completed ? $"https://example.test/pull/{id}" : null, retryAfter), At(minute));

    private static Ended Unreachable(string id) =>
        new(id, new TaskOutcome(id, TaskState.NotStarted, null, "runner down", ErrorCode.ContainerUnavailable, Replace: true), T0);

    private static readonly ScheduleEvent[] Running2 = [Tick(), Admit("t1"), Admit("t2")];

    private static ScheduleEvent[] RateLimited(DateTimeOffset? reset) => [Tick(), Admit("t1"), End("t1", TaskState.Failed, error: ErrorCode.RateLimited, retryAfter: reset)];

    private sealed record Row(SchedulePlan Plan, ScheduleEvent[] Events, string[] Then);

    private static readonly Dictionary<string, Row> Rows = new()
    {
        // Order and parallelism.
        ["the first tick places the first task"] = new(Plan(), [Tick()], ["place t1"]),
        ["an admitted task starts and the next is placed"] = new(Plan(), [Tick(), Admit("t1")], ["start t1", "place t2"]),
        ["at max parallel the batch waits for its tasks"] = new(Plan(), Running2, ["start t2", "await tasks"]),
        ["an end asks for nothing by itself"] = new(Plan(), [.. Running2, End("t1")], ["finish t1 Completed"]),
        ["the tick after an end places the next task in order"] = new(Plan(), [.. Running2, End("t1"), Tick()], ["place t3"]),
        ["the last end and its tick end the batch"] = new(Plan(1, 1), [Tick(), Admit("t1"), End("t1"), Tick()], ["done"]),

        // Caps: a running task reserves its whole cap; spend replaces the reservation.
        ["two running tasks reserve their caps so a third waits"] = new(Plan(3, 3, tokens: 5000, perTask: 2000), Running2, ["start t2", "await tasks"]),
        ["real spend replaces the reservation"] = new(Plan(3, 3, tokens: 5000, perTask: 2000),
            [.. Running2, Report("t1", 2000), Report("t2", 2000), End("t1", tokens: 1500), End("t2", tokens: 1500), Tick()], ["place t3"]),
        ["a task that can never fit ends the batch"] = new(Plan(3, 1, tokens: 2500, perTask: 2000), [Tick(), Admit("t1"), End("t1", tokens: 1800), Tick()], ["done"]),
        ["the dollar cap holds a second task"] = new(Plan(2, 2, usd: 3m, perTaskUsd: 2m), [Tick(), Admit("t1")], ["start t1", "await tasks"]),
        ["known dollar spend lets the next in"] = new(Plan(2, 2, usd: 3m, perTaskUsd: 2m), [Tick(), Admit("t1"), End("t1", usd: 0.5m), Tick()], ["place t2"]),
        ["a task over its own token cap is cancelled"] = new(Plan(), [.. Running2, Report("t1", 1200)], ["cancel t1"]),
        ["a task over its cap is cancelled once"] = new(Plan(), [.. Running2, Report("t1", 1200), Report("t1", 1300)], []),
        ["a task over its own dollar cap is cancelled"] = new(Plan(1, 1, perTaskUsd: 2m), [Tick(), Admit("t1"), Report("t1", 10, 2.5m)], ["cancel t1"]),
        ["the batch exactly at its cap cancels nothing"] = new(Plan(2, 2, tokens: 3000, perTask: 1500), [.. Running2, Report("t1", 1500), Report("t2", 1500)], []),
        ["one token past the batch cap cancels every running task"] = new(Plan(2, 2, tokens: 3000, perTask: 1500),
            [.. Running2, Report("t1", 1500), Report("t2", 1500), Report("t1", 1501)], ["cancel t1", "cancel t1", "cancel t2"]),
        ["a batch over by more than a task cap cancels them all"] = new(Plan(2, 2, tokens: 3000), [.. Running2, Report("t1", 2500), Report("t2", 2500)],
            ["cancel t2", "cancel t1", "cancel t2"]),
        ["a task cancelled for its cap ends failed on the cap"] = new(Plan(), [.. Running2, Report("t1", 1200), End("t1", TaskState.Cancelled, tokens: 0)],
            ["finish t1 Failed CostCapReached"]),
        ["a task ending after an overspend carries the cap"] = new(Plan(2, 2, tokens: 3000, perTask: 3000),
            [.. Running2, Report("t1", 2000), Report("t2", 2000), End("t2", TaskState.Cancelled)], ["finish t2 Cancelled CostCapReached"]),
        ["a completed task keeps its outcome after an overspend"] = new(Plan(2, 2, tokens: 3000, perTask: 3000),
            [.. Running2, Report("t1", 2000), Report("t2", 2000), End("t2")], ["finish t2 Completed"]),
        ["a report from a task that is not running is ignored"] = new(Plan(), [Tick(), Report("t1", 5000)], []),

        // Rate limits: one pauses new starts, running tasks go on; the reset counts only past the backoff and within the longest pause.
        ["a rate limit with a reset pauses new starts until it"] = new(Plan(), [.. Running2, End("t1", TaskState.Failed, error: ErrorCode.RateLimited, retryAfter: At(20))],
            ["finish t1 Failed RateLimited", "pause t1 until +20m"]),
        ["running tasks go on through a pause"] = new(Plan(), [.. Running2, End("t1", TaskState.Failed, error: ErrorCode.RateLimited, retryAfter: At(20)), Tick()],
            ["await tasks"]),
        ["with nothing running the batch waits for the reset"] = new(Plan(),
            [.. Running2, End("t1", TaskState.Failed, error: ErrorCode.RateLimited, retryAfter: At(20)), End("t2"), Tick(1)], ["wait until +20m"]),
        ["the reset lets the next task start"] = new(Plan(),
            [.. Running2, End("t1", TaskState.Failed, error: ErrorCode.RateLimited, retryAfter: At(20)), End("t2"), Tick(20)], ["place t3"]),
        ["a second before the reset still waits"] = new(Plan(),
            [.. Running2, End("t1", TaskState.Failed, error: ErrorCode.RateLimited, retryAfter: At(20)), End("t2"), Tick(20 - (1.0 / 60))], ["wait until +20m"]),
        ["a rate limit with no reset pauses for the backoff"] = new(Plan(2, 1), RateLimited(null), ["finish t1 Failed RateLimited", "pause t1 until +1m"]),
        ["a reset already past pauses for the backoff"] = new(Plan(2, 1), RateLimited(At(-60)), ["finish t1 Failed RateLimited", "pause t1 until +1m"]),
        ["a reset sooner than the backoff waits for the backoff"] = new(Plan(2, 1), RateLimited(T0.AddSeconds(5)), ["finish t1 Failed RateLimited", "pause t1 until +1m"]),
        ["a reset at the longest pause still pauses"] = new(Plan(2, 1), RateLimited(At(360)), ["finish t1 Failed RateLimited", "pause t1 until +360m"]),
        ["a reset past the longest pause stops the batch"] = new(Plan(2, 1), RateLimited(At(361)), ["finish t1 Failed RateLimited"]),
        ["a second rate limit in a row stops the batch"] = new(Plan(4, 1),
            [.. RateLimited(null), Tick(1), Admit("t2", 1), End("t2", TaskState.Failed, error: ErrorCode.RateLimited, minute: 1)], ["finish t2 Failed RateLimited"]),
        ["a completion between two rate limits resets the count"] = new(Plan(4, 1),
            [.. RateLimited(null), Tick(1), Admit("t2", 1), End("t2", minute: 1), Tick(1), Admit("t3", 1), End("t3", TaskState.Failed, error: ErrorCode.RateLimited, minute: 1)],
            ["finish t3 Failed RateLimited", "pause t3 until +2m"]),
        ["an unavailable provider stops new starts and what runs goes on"] = new(Plan(4, 2),
            [.. Running2, End("t1", TaskState.Failed, error: ErrorCode.ProviderUnavailable), Tick()], ["await tasks"]),

        // Dependencies.
        ["a task waits for its dependency although a slot is free"] = new(Plan([Dep("t1"), Dep("t2", "t1"), Dep("t3")], 3), [Tick(), Admit("t1")], ["start t1", "place t3"]),
        ["a dependent does not start while its dependency runs"] = new(Plan([Dep("t1"), Dep("t2", "t1"), Dep("t3")], 3),
            [Tick(), Admit("t1"), Admit("t3"), End("t3"), Tick()], ["await tasks"]),
        ["a completed dependency lets its dependent start"] = new(Plan([Dep("t1"), Dep("t2", "t1"), Dep("t3")], 3),
            [Tick(), Admit("t1"), Admit("t3"), End("t3"), End("t1"), Tick()], ["place t2"]),
        ["a task listed before its dependency waits for it"] = new(Plan([Dep("b", "a"), Dep("a")]), [Tick()], ["place a"]),
        ["a failed dependency skips its dependents down the chain"] = new(Plan([Dep("t1"), Dep("t2", "t1"), Dep("t3", "t2"), Dep("t4")], 1),
            [Tick(), Admit("t1"), End("t1", TaskState.Failed), Tick()], ["skip t2 NotStarted", "skip t3 NotStarted", "place t4"]),
        ["a cancelled dependency skips its dependent as cancelled"] = new(Plan([Dep("t1"), Dep("t2", "t1"), Dep("t3", "t2"), Dep("t4")], 1),
            [Tick(), Admit("t1"), End("t1", TaskState.Cancelled), Tick()], ["skip t2 NotStarted Cancelled", "skip t3 NotStarted", "place t4"]),
        ["a dependency cycle ends the batch"] = new(Plan([Dep("a", "b"), Dep("b", "a")]), [Tick()], ["done"]),

        // Operator cancels.
        ["an operator cancel keeps a pending task from starting"] = new(Plan(2, 1), [new OperatorCancelled("t2"), Tick()], ["skip t2 Cancelled Cancelled", "place t1"]),
        ["an operator cancel of a running task leaves it to its runner"] = new(Plan(2, 1), [Tick(), Admit("t1"), new OperatorCancelled("t1"), Tick()], ["await tasks"]),
        ["an operator cancel by itself asks for nothing"] = new(Plan(2, 1), [new OperatorCancelled("t1")], []),

        // A runner that did not answer: placed again, up to three placements.
        ["a task whose runner did not answer is placed again"] = new(Plan(1, 1), [Tick(), Admit("t1", runner: "r1"), Unreachable("t1")], ["hold t1: runner down"]),
        ["the tick after a replace places it"] = new(Plan(1, 1), [Tick(), Admit("t1", runner: "r1"), Unreachable("t1"), Tick()], ["place t1"]),
        ["the third placement's silence fails the task"] = new(Plan(1, 1),
            [Tick(), Admit("t1"), Unreachable("t1"), Tick(), Admit("t1"), Unreachable("t1"), Tick(), Admit("t1"), Unreachable("t1")], ["finish t1 Failed ContainerUnavailable"]),
        ["a halt fails a task that asks to be placed again"] = new(Plan(1, 1), [Tick(), Admit("t1"), new Halt(), Unreachable("t1")], ["finish t1 Failed ContainerUnavailable"]),

        // The host.
        ["an admitted task starts on the runner the host chose"] = new(Plan(admission: true), [Tick(), Admit("t1", runner: "r1")], ["start t1 on r1", "place t2"]),
        ["a task let in against the rule says why first"] = new(Plan(admission: true), [Tick(), Admit("t1", forced: "nothing else ran")],
            ["hold t1: nothing else ran", "start t1", "place t2"]),
        ["a held task is reported and looked at again"] = new(Plan(admission: true), [Tick(), new Held("t1", "short of memory", T0)], ["hold t1: short of memory", "look again"]),
        ["the host is not asked again before its interval"] = new(Plan(admission: true), [Tick(), new Held("t1", "short of memory", T0), Tick(0.25)], ["look again"]),
        ["after the interval the task is placed again"] = new(Plan(admission: true), [Tick(), new Held("t1", "short of memory", T0), Tick(1)], ["place t1"]),
        ["the same task held again is not reported again"] = new(Plan(admission: true),
            [Tick(), new Held("t1", "short of memory", T0), Tick(1), new Held("t1", "short of memory", At(1))], ["look again"]),
        ["a held task with others running waits for them"] = new(Plan(admission: true), [Tick(), Admit("t1"), new Held("t2", "short of memory", T0)],
            ["hold t2: short of memory", "await tasks"]),
        ["a refused task ends the batch"] = new(Plan(admission: true), [Tick(), new Refused("t1", "no runner answered", T0)], ["done"]),
        ["a blocked placement with nothing running looks again"] = new(Plan(), [Tick(), new Blocked(T0)], ["look again"]),
        ["a blocked placement with tasks running waits for them"] = new(Plan(), [Tick(), Admit("t1"), new Blocked(T0)], ["await tasks"]),

        // A halt.
        ["a halt with nothing running ends the batch"] = new(Plan(), [new Halt(), Tick()], ["done"]),
        ["a halt with tasks running waits for them"] = new(Plan(), [Tick(), Admit("t1"), new Halt(), Tick()], ["await tasks"]),
        ["a halt by itself asks for nothing"] = new(Plan(), [Tick(), new Halt()], []),
    };

    public static TheoryData<string> Names() => new(Rows.Keys);

    [Theory]
    [MemberData(nameof(Names))]
    public void Decides(string row)
    {
        var (_, last) = Fold(Rows[row].Plan, Rows[row].Events);
        Assert.Equal(Rows[row].Then, last.Select(Show));
    }

    /// <summary>A restart (ADR 0063): <see cref="Replayed"/> is what the journal kept, folded with <see cref="BatchMachine.Replay"/>; <see cref="Live"/> follows in the new process, and the row checks
    /// what its last event asks for.</summary>
    private sealed record ReplayRow(SchedulePlan Plan, ScheduleEvent[] Replayed, ScheduleEvent[] Live, string[] Then);

    private static Ended RateLimit(string id, double minute = 0, DateTimeOffset? reset = null) => End(id, TaskState.Failed, error: ErrorCode.RateLimited, retryAfter: reset, minute: minute);

    private static readonly Dictionary<string, ReplayRow> ReplayRows = new()
    {
        ["nothing kept: the restart starts the batch from the start"] = new(Plan(), [], [Tick(30)], ["place t1"]),
        ["a kept end is not placed again"] = new(Plan(2, 1), [End("t1")], [Tick(30)], ["place t2"]),
        ["a kept rate limit holds new starts until its recorded reset, not one counted from the restart"] = new(Plan(), [RateLimit("t1", reset: At(20))], [Tick(5)], ["wait until +20m"]),
        ["a kept rate limit with no reset holds for the backoff from when it was hit"] = new(Plan(), [RateLimit("t1")], [Tick(0.5)], ["wait until +1m"]),
        ["a pause that lifted before the restart holds nothing"] = new(Plan(), [RateLimit("t1", reset: At(20))], [Tick(30)], ["place t2"]),
        ["a kept strike makes the next rate limit stop the batch"] = new(Plan(3, 1), [RateLimit("t1")], [Tick(2), Admit("t2", 2), RateLimit("t2", 2)], ["finish t2 Failed RateLimited"]),
        ["a kept completion after a rate limit resets the strikes"] = new(Plan(3, 1), [RateLimit("t1"), End("t2", minute: 1)], [Tick(2), Admit("t3", 2), RateLimit("t3", 2)],
            ["finish t3 Failed RateLimited", "pause t3 until +3m"]),
        ["two kept rate limits in a row leave the batch stopped"] = new(Plan(3, 1), [RateLimit("t1"), RateLimit("t2", 1)], [Tick(30)], ["done"]),
        ["kept placements count towards the third"] = new(Plan(1, 1), [Unreachable("t1"), Unreachable("t1")], [Tick(), Admit("t1"), Unreachable("t1")],
            ["finish t1 Failed ContainerUnavailable"]),
        ["one kept placement leaves two"] = new(Plan(1, 1), [Unreachable("t1")], [Tick(), Admit("t1"), Unreachable("t1")], ["hold t1: runner down"]),
        ["a kept operator cancel keeps the pending task from starting"] = new(Plan(2, 1), [new OperatorCancelled("t2")], [Tick()], ["skip t2 Cancelled Cancelled", "place t1"]),
        ["a kept failure skips its dependents at the first look"] = new(Plan([Dep("t1"), Dep("t2", "t1"), Dep("t3")], 1), [End("t1", TaskState.Failed)], [Tick()],
            ["skip t2 NotStarted", "place t3"]),
        ["a kept end's spend counts towards the batch's cap"] = new(Plan(2, 1, tokens: 2500, perTask: 2000), [End("t1", tokens: 1800)], [Tick()], ["done"]),
        ["an event that is not kept is skipped on replay"] = new(Plan(2, 1), [Tick(), Admit("t1"), Report("t1", 5000), new Halt()], [Tick()], ["place t1"]),
        ["a kept end of a task the plan does not have is skipped"] = new(Plan(2, 1), [End("t9"), new OperatorCancelled("t9")], [Tick()], ["place t1"]),
        ["a second kept end of an ended task is skipped"] = new(Plan(3, 1), [End("t1"), RateLimit("t1")], [Tick()], ["place t2"]),
    };

    public static TheoryData<string> ReplayNames() => new(ReplayRows.Keys);

    [Theory]
    [MemberData(nameof(ReplayNames))]
    public void Replays(string row)
    {
        var (plan, replayed, live, then) = ReplayRows[row];
        var (state, _) = BatchMachine.Replay(plan, replayed);
        IReadOnlyList<ScheduleCommand> last = [];
        foreach (var e in live)
        {
            var decision = BatchMachine.Decide(state, e);
            state = decision.State;
            last = decision.Commands;
        }
        Assert.Equal(then, last.Select(Show));
    }

    [Fact]
    public void A_replay_carries_out_nothing_and_returns_the_pause_to_tell_again()
    {
        var (state, pause) = BatchMachine.Replay(Plan(), [End("t1"), RateLimit("t2", 1, At(20))]);
        Assert.Equal(("t2", At(20)), (pause!.TaskId, pause.Until));
        Assert.Equal((At(20), 1, 200L), (state.PausedUntil, state.Strikes, state.DoneTokens));
        Assert.Equal([SlotPhase.Ended, SlotPhase.Ended, SlotPhase.Pending], state.Slots.Select(x => x.Phase));
        Assert.Null(BatchMachine.Replay(Plan(), [End("t1")]).Pause);
    }

    [Fact]
    public void The_journaled_events_round_trip_and_a_kind_or_payload_it_does_not_know_is_skipped()
    {
        var ended = End("t1", TaskState.Failed, tokens: 1200, usd: 0.75m, error: ErrorCode.RateLimited, retryAfter: At(20), minute: 3) is var e
            ? e with { Outcome = e.Outcome with { Runner = "r1", Detail = "limit" } } : null!;
        var (kind, payload) = ScheduleEventJson.Encode(ended);
        Assert.Equal("ended", kind);
        Assert.Equal(ended, ScheduleEventJson.Decode(kind, payload));
        var (cancelKind, cancelPayload) = ScheduleEventJson.Encode("t2", At(4));
        Assert.Equal(("operator_cancelled", """{"task":"t2","at":"2026-10-14T12:04:00+00:00"}"""), (cancelKind, cancelPayload));
        Assert.Equal(new OperatorCancelled("t2"), ScheduleEventJson.Decode(cancelKind, cancelPayload));
        Assert.Contains("\"state\":\"failed\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"error\":\"rate_limited\"", payload, StringComparison.Ordinal);
        Assert.Null(ScheduleEventJson.Decode("admitted", """{"task":"t1"}"""));
        Assert.Null(ScheduleEventJson.Decode("ended", """{"task":"t1","at":"2026-10-14T12:04:00+00:00"}"""));
        Assert.Null(ScheduleEventJson.Decode("ended", """{"task":"t1","outc"""));
        Assert.Null(ScheduleEventJson.Decode("operator_cancelled", """{"at":"2026-10-14T12:04:00+00:00"}"""));
    }

    [Fact]
    public void A_task_that_can_never_fit_and_the_rest_are_not_started_on_the_cap()
    {
        var outcome = Outcome(Plan(3, 1, tokens: 2500, perTask: 2000), [Tick(), Admit("t1"), End("t1", tokens: 1800), Tick()]);
        Assert.Equal([TaskState.Completed, TaskState.NotStarted, TaskState.NotStarted], outcome.Tasks.Select(t => t.State));
        Assert.All(outcome.Tasks.Skip(1), t => Assert.Equal("not started: the batch's cap leaves no room for another task", t.Detail));
        Assert.All(outcome.Tasks.Skip(1), t => Assert.Equal(ErrorCode.CostCapReached, t.Error));
        Assert.Equal(1800, outcome.Tokens);
    }

    [Fact]
    public void A_task_stopped_for_its_cap_keeps_what_it_reported()
    {
        var (state, last) = Fold(Plan(), [.. Running2, Report("t1", 1200), End("t1", TaskState.Cancelled, tokens: 0)]);
        var finished = Assert.IsType<Finish>(Assert.Single(last)).Outcome;
        Assert.Equal((1200L, "stopped: the task's cap was exceeded"), (finished.Tokens, finished.Detail));
        Assert.Equal(1200, state.DoneTokens);
    }

    [Fact]
    public void An_overspend_names_the_batch_cap_on_what_ends_and_on_what_never_started()
    {
        var events = new ScheduleEvent[] { Tick(), Admit("t1"), Admit("t2"), Report("t1", 2500), Report("t2", 2500), End("t1", TaskState.Cancelled), End("t2", TaskState.Cancelled), Tick() };
        var outcome = Outcome(Plan(3, 2, tokens: 3000), events);
        Assert.Equal(["stopped: the batch's cap was exceeded", "stopped: the batch's cap was exceeded", "not started: the batch's cap was used up"], outcome.Tasks.Select(t => t.Detail));
        Assert.All(outcome.Tasks, t => Assert.Equal(ErrorCode.CostCapReached, t.Error));
    }

    [Fact]
    public void Two_rate_limits_in_a_row_end_the_batch_rate_limited_with_the_rest_not_started()
    {
        var outcome = Outcome(Plan(4, 1), [.. RateLimited(null), Tick(1), Admit("t2", 1), End("t2", TaskState.Failed, error: ErrorCode.RateLimited, minute: 1), Tick(1)]);
        Assert.Equal(ErrorCode.RateLimited, outcome.Error);
        Assert.Equal(BatchMachine.RepeatedLimitAction, outcome.Action);
        Assert.Equal([TaskState.Failed, TaskState.Failed, TaskState.NotStarted, TaskState.NotStarted], outcome.Tasks.Select(t => t.State));
        Assert.All(outcome.Tasks.Skip(2), t => Assert.Equal((ErrorCode.RateLimited, "not started: the provider's rate limit was hit twice in a row"), (t.Error!.Value, t.Detail)));
    }

    [Fact]
    public void A_reset_past_the_longest_pause_says_how_to_switch_to_an_api_key()
    {
        var outcome = Outcome(Plan(2, 1), [.. RateLimited(At(7 * 60)), Tick()]);
        Assert.Equal(TaskState.NotStarted, outcome.Tasks[1].State);
        Assert.Contains("api_key_secret", outcome.Action);
        Assert.Equal(ErrorCode.RateLimited, outcome.Error);
    }

    [Fact]
    public void An_unavailable_provider_stops_the_batch_without_fallback()
    {
        var outcome = Outcome(Plan(4, 2), [.. Running2, End("t1", TaskState.Failed, error: ErrorCode.ProviderUnavailable), Tick(), End("t2"), Tick()]);
        Assert.Equal([TaskState.Failed, TaskState.Completed, TaskState.NotStarted, TaskState.NotStarted], outcome.Tasks.Select(t => t.State));
        Assert.All(outcome.Tasks.Skip(2), t => Assert.Equal("not started: the provider is rate limited or unavailable", t.Detail));
        Assert.Contains("API key", outcome.Action);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void A_failed_task_does_not_stop_the_others_but_the_batch_is_not_complete()
    {
        var outcome = Outcome(Plan(3, 1), [Tick(), Admit("t1"), End("t1", TaskState.Failed, error: ErrorCode.VerificationFailed), Tick(), Admit("t2"), End("t2", TaskState.NeedsInput),
            Tick(), Admit("t3"), End("t3"), Tick()]);
        Assert.Equal([TaskState.Failed, TaskState.NeedsInput, TaskState.Completed], outcome.Tasks.Select(t => t.State));
        Assert.Equal(["t1", "t2"], outcome.Incomplete);
    }

    [Fact]
    public void A_halt_leaves_the_rest_not_started_as_cancelled()
    {
        var outcome = Outcome(Plan(2, 1), [Tick(), Admit("t1"), new Halt(), End("t1", TaskState.Cancelled), Tick()]);
        Assert.Equal(TaskState.NotStarted, outcome.Tasks[1].State);
        Assert.Equal(("not started: the batch was cancelled", ErrorCode.Cancelled), (outcome.Tasks[1].Detail, outcome.Tasks[1].Error!.Value));
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void A_refused_task_is_not_started_with_the_hosts_reason()
    {
        var outcome = Outcome(Plan(2, admission: true), [Tick(), new Refused("t1", "no runner answered", T0)]);
        Assert.All(outcome.Tasks, t => Assert.Equal(("not started: no runner answered", ErrorCode.ContainerUnavailable), (t.Detail, t.Error!.Value)));
    }

    [Fact]
    public void Dependents_say_which_dependency_did_not_complete_and_how()
    {
        var plan = Plan([Dep("t1"), Dep("t2", "t1"), Dep("t3", "t2")], 1);
        var (_, last) = Fold(plan, [Tick(), Admit("t1"), End("t1", TaskState.NeedsInput), Tick()]);
        Assert.Equal(["not started: the task it depends on, 't1', asked a question", "not started: the task it depends on, 't2', was not started"],
            last.OfType<Skip>().Select(s => s.Outcome.Detail));
    }

    [Fact]
    public void A_dependency_cycle_is_not_started_with_the_reason()
    {
        var outcome = Outcome(Plan([Dep("a", "b"), Dep("b", "a")]), [Tick()]);
        Assert.All(outcome.Tasks, t => Assert.Equal("not started: the tasks it depends on can never complete", t.Detail));
    }

    [Fact]
    public void A_dependent_starts_with_its_dependencies_branches_in_the_order_it_lists_them()
    {
        var plan = Plan([Dep("t1"), Dep("t2"), Dep("t3", "t2", "t1")], 3);
        var (_, last) = Fold(plan, [Tick(), Admit("t1"), Admit("t2"), End("t1"), End("t2"), Tick(), Admit("t3", runner: "r2")]);
        var task = last.OfType<Start>().Single().Task;
        Assert.Equal([new UpstreamWork("t2", "run-t2", "chargehand/run-t2", "https://example.test/pull/t2"), new UpstreamWork("t1", "run-t1", "chargehand/run-t1", "https://example.test/pull/t1")],
            task.Upstream!);
        Assert.Equal("r2", task.Runner);
    }

    [Fact]
    public void The_batch_reports_in_the_order_it_was_given()
    {
        var outcome = Outcome(Plan([Dep("b", "a"), Dep("a")]), [Tick(), Admit("a"), End("a"), Tick(), Admit("b"), End("b"), Tick()]);
        Assert.Equal(["b", "a"], outcome.Tasks.Select(t => t.Id));
        Assert.True(outcome.AllCompleted);
    }

    [Fact]
    public void Only_a_decision_that_needs_the_shell_ends_with_a_step()
    {
        var plan = Plan();
        var state = BatchMachine.Begin(plan);
        Assert.IsType<Place>(BatchMachine.Decide(state, Tick()).Step);
        Assert.Null(BatchMachine.Decide(state, Report("t1", 10)).Step);
        Assert.Null(BatchMachine.Decide(state, new Halt()).Step);
    }

    private static (ScheduleState State, IReadOnlyList<ScheduleCommand> Last) Fold(SchedulePlan plan, IEnumerable<ScheduleEvent> events)
    {
        var state = BatchMachine.Begin(plan);
        IReadOnlyList<ScheduleCommand> last = [];
        foreach (var e in events)
        {
            var decision = BatchMachine.Decide(state, e);
            state = decision.State;
            last = decision.Commands;
        }
        return (state, last);
    }

    private static BatchOutcome Outcome(SchedulePlan plan, IEnumerable<ScheduleEvent> events) => Assert.IsType<Done>(Fold(plan, events).Last[^1]).Outcome;

    private static string Show(ScheduleCommand command) => command switch
    {
        Place p => $"place {p.TaskId}",
        Start s => s.PlacedOn is null ? $"start {s.TaskId}" : $"start {s.TaskId} on {s.PlacedOn}",
        Cancel c => $"cancel {c.TaskId}",
        Skip s => $"skip {s.Outcome.Id} {Show(s.Outcome)}",
        Finish f => $"finish {f.Outcome.Id} {Show(f.Outcome)}",
        Hold h => $"hold {h.TaskId}: {h.Why}",
        Pause p => $"pause {p.TaskId} until {Minutes(p.Until)}",
        AwaitTasks => "await tasks",
        WaitUntil w => $"wait until {Minutes(w.Until)}",
        LookAgain => "look again",
        Done => "done",
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };

    private static string Show(TaskOutcome outcome) => outcome.Error is { } error ? $"{outcome.State} {error}" : $"{outcome.State}";

    private static string Minutes(DateTimeOffset at) => string.Create(CultureInfo.InvariantCulture, $"+{(at - T0).TotalMinutes}m");
}
