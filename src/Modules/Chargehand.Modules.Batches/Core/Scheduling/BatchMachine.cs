using Chargehand.Contracts;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Core.Scheduling;

/// <summary>The scheduler's decisions (ADR 0039, ADR 0060 rule 2): state and an event in, the next state and the commands out, with no I/O and no clock.
/// Per task: pending, then skipped (an operator cancel, a dependency that did not complete) or held by the host or running; a running task ends, and is placed again
/// (back to pending, up to <see cref="MaxPlacements"/> placements) or finished with its outcome rewritten for the task's and the batch's caps. Per batch: a rate limit pauses new starts,
/// a second one in a row or a reset past <see cref="MaxPause"/> stops them, an unavailable provider stops them, and whatever never started is not started.</summary>
internal static class BatchMachine
{
    /// <summary>How many runners a task is placed on before its runner's silence is the task's failure.</summary>
    public const int MaxPlacements = 3;

    /// <summary>The longest wait for a limit to lift; past it the batch stops and the caller retries.</summary>
    public static readonly TimeSpan MaxPause = TimeSpan.FromHours(6);

    public const string RateLimitedAction =
        "The subscription is rate limited or unavailable, so the rest of the batch was not started. Retry later, or switch the profile to an API key "
        + "(claude_code.api_key_secret in place of oauth_token_secret) and resend the tasks that did not finish.";

    public const string RepeatedLimitAction =
        "The provider's usage or rate limit was hit twice in a row with no task finishing in between, so the rest of the batch was not started. "
        + "Resend the tasks that did not finish once the limit lifts.";

    public static ScheduleState Begin(SchedulePlan plan) => new(plan, [.. plan.Tasks.Select(_ => new TaskSlot())], []);

    public static ScheduleDecision Decide(ScheduleState state, ScheduleEvent input)
    {
        var commands = new List<ScheduleCommand>();
        var next = input switch
        {
            Tick tick => Step(Sweep(state, commands), tick.Now, commands),
            OperatorCancelled cancel => state with { CancelRequested = state.CancelRequested.Add(cancel.TaskId) },
            Admitted admitted => Step(Admit(state, admitted, commands), admitted.Now, commands),
            Held held => Idle(Wait(state, held, commands), held.Now, commands),
            Refused refused => Idle(state with
            {
                StopReason = state.StopReason ?? $"not started: {refused.Detail}",
                StopError = state.StopError ?? ErrorCode.ContainerUnavailable,
            }, refused.Now, commands),
            Blocked blocked => Idle(state, blocked.Now, commands),
            Reported reported => Report(state, reported, commands),
            Ended ended => ended.Outcome.Replace ? PlaceAgain(state, ended, commands) : Close(state, ended, commands),
            Halt => state with { Halted = true },
            _ => throw new ArgumentOutOfRangeException(nameof(input), input, "an event the batch machine does not know"),
        };
        return new ScheduleDecision(next, commands);
    }

    /// <summary>The state a restart resumes from (ADR 0063): the journaled events folded from <see cref="Begin"/> in the order they were appended, with none of their commands carried out.
    /// Only <see cref="OperatorCancelled"/> and <see cref="Ended"/> are journaled; any other event, a task the plan does not have and a second end of a task that ended are skipped. The last
    /// <see cref="Pause"/> the ends asked for comes back with the state, for the shell to tell again.</summary>
    public static (ScheduleState State, Pause? Pause) Replay(SchedulePlan plan, IEnumerable<ScheduleEvent> journaled)
    {
        var state = Begin(plan);
        Pause? pause = null;
        foreach (var e in journaled)
        {
            if (!Replays(state, e))
                continue;
            var decision = Decide(state, e);
            state = decision.State;
            pause = decision.Commands.OfType<Pause>().LastOrDefault() ?? pause;
        }
        return (state, pause);
    }

    private static bool Replays(ScheduleState s, ScheduleEvent e) => e switch
    {
        OperatorCancelled cancel => s.Plan.IndexOf.ContainsKey(cancel.TaskId),
        Ended ended => s.Plan.IndexOf.TryGetValue(ended.TaskId, out var i) && s.Slots[i].Phase != SlotPhase.Ended,
        _ => false,
    };

    /// <summary>The ids of the tasks that have not started, in order: the ones an operator cancel can still keep from starting.</summary>
    public static IEnumerable<string> Pending(ScheduleState state) =>
        Enumerable.Range(0, state.Slots.Length).Where(state.IsPending).Select(i => state.Plan.Tasks[i].Id);

    // Start what fits, in order; else say what to wait for.
    private static ScheduleState Step(ScheduleState s, DateTimeOffset now, List<ScheduleCommand> commands)
    {
        if (s.StopReason is null && !s.Halted && s.RunningCount < s.Plan.MaxParallel && now >= s.PausedUntil && NextReady(s) is { } index && Fits(s) && now >= s.AdmitAfter)
        {
            var task = s.Plan.Tasks[index];
            commands.Add(new Place(task.Id, task.Runner));
            return s;
        }
        return Idle(s, now, commands);
    }

    // Nothing more starts now: wait for what runs, for a limit to lift or for a slot, or end the batch.
    private static ScheduleState Idle(ScheduleState s, DateTimeOffset now, List<ScheduleCommand> commands)
    {
        if (s.RunningCount > 0)
        {
            commands.Add(new AwaitTasks());
            return s;
        }
        if (s.StopReason is null && !s.Halted && s.Slots.Any(x => x.Phase == SlotPhase.Pending))
        {
            if (NextReady(s) is null)
            {
                // Every dependency has ended and none is waiting to start, so what is left depends in a circle (a request is checked for that before it gets here).
                s = s with { StopReason = "not started: the tasks it depends on can never complete" };
            }
            else if (!Fits(s))
            {
                s = s with { StopReason = "the batch's cap leaves no room for another task", StopError = ErrorCode.CostCapReached };
            }
            else
            {
                // A usage or rate limit holds new starts, or only a shared slot or the host is in the way (another batch holds the slots).
                commands.Add(s.PausedUntil > now ? new WaitUntil(s.PausedUntil) : new LookAgain());
                return s;
            }
        }
        commands.Add(new Done(Outcome(s)));
        return s;
    }

    // A task whose dependency ended any way but completed will never start; that spreads down the chain. One an operator cancelled never starts either.
    private static ScheduleState Sweep(ScheduleState s, List<ScheduleCommand> commands)
    {
        bool again;
        do
        {
            again = false;
            for (var i = 0; i < s.Slots.Length; i++)
            {
                if (!s.IsPending(i) || SkipOf(s, i) is not { } skipped)
                    continue;
                s = s.WithSlot(i, s.Slots[i] with { Phase = SlotPhase.Ended, Outcome = skipped });
                commands.Add(new Skip(skipped));
                again = true;
            }
        }
        while (again);
        return s;
    }

    private static TaskOutcome? SkipOf(ScheduleState s, int i)
    {
        var id = s.Plan.Tasks[i].Id;
        if (s.CancelRequested.Contains(id))
            return new TaskOutcome(id, TaskState.Cancelled, null, "cancelled by the operator before it started", ErrorCode.Cancelled);
        var cause = s.Plan.Dependencies[i].Select(d => s.Slots[d].Outcome).FirstOrDefault(o => o is { State: not TaskState.Completed });
        if (cause is null)
            return null;
        var how = cause.State switch
        {
            TaskState.Failed => "failed",
            TaskState.NeedsInput => "asked a question",
            TaskState.Cancelled => "was cancelled",
            _ => "was not started",
        };
        return new TaskOutcome(id, TaskState.NotStarted, null, $"not started: the task it depends on, '{cause.Id}', {how}", cause.State == TaskState.Cancelled ? ErrorCode.Cancelled : null);
    }

    // The first task, in order, that is not started and whose dependencies all completed; null: none is ready now.
    private static int? NextReady(ScheduleState s)
    {
        for (var i = 0; i < s.Slots.Length; i++)
            if (s.IsPending(i) && s.Plan.Dependencies[i].All(d => s.Slots[d].Outcome?.State == TaskState.Completed))
                return i;
        return null;
    }

    // "Afford" reserves each running task's whole cap, or what it reported when that is more.
    private static bool Fits(ScheduleState s)
    {
        var plan = s.Plan;
        var running = s.Running.Select(i => s.Slots[i]).ToList();
        if (plan.MaxTokensTotal is { } tokenCap && s.DoneTokens + running.Sum(r => Math.Max(plan.PerTask.MaxTokens, r.Tokens)) + plan.PerTask.MaxTokens > tokenCap)
            return false;
        return plan.MaxUsdTotal is not { } usdCap || plan.PerTask.MaxUsd is not { } perTask || s.DoneUsd + running.Sum(r => Math.Max(perTask, r.Usd)) + perTask <= usdCap;
    }

    private static ScheduleState Admit(ScheduleState s, Admitted admitted, List<ScheduleCommand> commands)
    {
        var index = s.Plan.IndexOf[admitted.TaskId];
        if (admitted.Forced is { } forced)
            commands.Add(new Hold(admitted.TaskId, forced));
        var task = WithUpstream(s, index) with { Runner = admitted.Runner ?? s.Plan.Tasks[index].Runner };
        commands.Add(new Start(admitted.TaskId, task, admitted.Runner));
        var slot = s.Slots[index] with { Phase = SlotPhase.Running, Runner = admitted.Runner, Tokens = 0, Usd = 0, OverTaskCap = false };
        return s.WithSlot(index, slot) with { Holding = null };
    }

    // The host has no room: ask again after its interval, and say so once while the same task waits.
    private static ScheduleState Wait(ScheduleState s, Held held, List<ScheduleCommand> commands)
    {
        if (s.Holding != held.TaskId)
            commands.Add(new Hold(held.TaskId, held.Detail));
        return s with { AdmitAfter = held.Now + s.Plan.AdmissionInterval.GetValueOrDefault(), Holding = held.TaskId };
    }

    private static ResolvedTask WithUpstream(ScheduleState s, int i)
    {
        var task = s.Plan.Tasks[i];
        if (task.DependsOn is not { Count: > 0 })
            return task;
        var upstream = s.Plan.Dependencies[i].Select(d => s.Slots[d].Outcome!).Where(o => o.RunId is not null && o.Branch is not null)
            .Select(o => new UpstreamWork(o.Id, o.RunId!, o.Branch!, o.PrUrl, o.Runner));
        return task with { Upstream = [.. upstream] };
    }

    // Live usage binds while the task runs: past its own cap the task is cancelled, and past the batch's cap every running task is.
    private static ScheduleState Report(ScheduleState s, Reported reported, List<ScheduleCommand> commands)
    {
        if (!s.Plan.IndexOf.TryGetValue(reported.TaskId, out var index) || s.Slots[index].Phase != SlotPhase.Running)
            return s;
        var slot = s.Slots[index] with { Tokens = reported.Tokens, Usd = reported.Usd };
        var cap = s.Plan.PerTask;
        if (!slot.OverTaskCap && (slot.Tokens > cap.MaxTokens || (cap.MaxUsd is { } usd && slot.Usd > usd)))
        {
            slot = slot with { OverTaskCap = true };
            commands.Add(new Cancel(reported.TaskId));
        }
        return Overspend(s.WithSlot(index, slot), commands);
    }

    private static ScheduleState Overspend(ScheduleState s, List<ScheduleCommand> commands)
    {
        var running = s.Running.ToList();
        var tokens = s.DoneTokens + running.Sum(i => s.Slots[i].Tokens);
        var usd = s.DoneUsd + running.Sum(i => s.Slots[i].Usd);
        var over = (s.Plan.MaxTokensTotal is { } t && tokens > t) || (s.Plan.MaxUsdTotal is { } u && usd > u);
        if (!over || s.Overspent)
            return s;
        commands.AddRange(running.Select(i => new Cancel(s.Plan.Tasks[i].Id)));
        return s with { Overspent = true, StopReason = s.StopReason ?? "the batch's cap was used up", StopError = s.StopError ?? ErrorCode.CostCapReached };
    }

    // Its runner did not answer before the session started: nothing ran, so the task takes no part in the caps and is placed again, elsewhere if it can be.
    private static ScheduleState PlaceAgain(ScheduleState s, Ended ended, List<ScheduleCommand> commands)
    {
        var index = s.Plan.IndexOf[ended.TaskId];
        var outcome = ended.Outcome;
        var placements = s.Slots[index].Placements + 1;
        if (placements < MaxPlacements && !s.Halted)
        {
            commands.Add(new Hold(outcome.Id, outcome.Detail ?? "its runner did not answer; placing it again"));
            return s.WithSlot(index, new TaskSlot(Placements: placements));
        }
        var failed = outcome with { State = TaskState.Failed, Replace = false };
        commands.Add(new Finish(failed));
        return s.WithSlot(index, new TaskSlot(SlotPhase.Ended, failed, Placements: placements));
    }

    private static ScheduleState Close(ScheduleState s, Ended ended, List<ScheduleCommand> commands)
    {
        var index = s.Plan.IndexOf[ended.TaskId];
        var slot = s.Slots[index];
        var outcome = ended.Outcome;
        if (outcome.Tokens == 0 && outcome.Usd == 0)
            outcome = outcome with { Tokens = slot.Tokens, Usd = slot.Usd };
        if (slot.OverTaskCap && outcome.State != TaskState.Completed)
            outcome = outcome with { State = TaskState.Failed, Error = ErrorCode.CostCapReached, Detail = "stopped: the task's cap was exceeded" };
        if (s.Overspent && outcome.State != TaskState.Completed)
            outcome = outcome with { Error = ErrorCode.CostCapReached, Detail = "stopped: the batch's cap was exceeded" };
        s = s.WithSlot(index, slot with { Phase = SlotPhase.Ended, Outcome = outcome }) with { DoneTokens = s.DoneTokens + outcome.Tokens, DoneUsd = s.DoneUsd + outcome.Usd };
        commands.Add(new Finish(outcome));
        if (outcome.State == TaskState.Completed)
            s = s with { Strikes = 0 };
        return Limit(s, outcome, ended.Now, commands);
    }

    // One limit pauses; a second with no task finished since means the pause did not help, so the batch ends and is resent after the reset.
    private static ScheduleState Limit(ScheduleState s, TaskOutcome outcome, DateTimeOffset now, List<ScheduleCommand> commands)
    {
        if (s.StopReason is not null)
            return s;
        if (outcome.Error is ErrorCode.ProviderUnavailable)
            return s with { StopReason = "not started: the provider is rate limited or unavailable", Action = RateLimitedAction };
        if (outcome.Error is not ErrorCode.RateLimited)
            return s;
        s = s with { Strikes = s.Strikes + 1 };
        if (s.Strikes >= 2)
            return LimitStopped(s, "not started: the provider's rate limit was hit twice in a row", RepeatedLimitAction);
        if (LimitLifts(s.Plan.Backoff, outcome, now) is not { } lifts)
            return LimitStopped(s, "not started: the provider is rate limited until later than the batch will wait", RateLimitedAction);
        var until = lifts > s.PausedUntil ? lifts : s.PausedUntil;
        commands.Add(new Pause(outcome.Id, until));
        return s with { PausedUntil = until };
    }

    private static ScheduleState LimitStopped(ScheduleState s, string reason, string action) =>
        s with { StopReason = reason, Action = action, StopError = ErrorCode.RateLimited, LimitStop = true };

    /// <summary>When new starts may resume after a rate-limited task: its reset time, but never sooner than the backoff (a reset that is unparsed, already past or too
    /// optimistic must not burn the batch); null when that is further off than <see cref="MaxPause"/>.</summary>
    private static DateTimeOffset? LimitLifts(TimeSpan backoff, TaskOutcome outcome, DateTimeOffset now)
    {
        var floor = now + backoff;
        var lifts = outcome.RetryAfter is { } reset && reset > floor ? reset : floor;
        return lifts - now > MaxPause ? null : lifts;
    }

    private static BatchOutcome Outcome(ScheduleState s)
    {
        var why = s.StopReason ?? (s.Halted ? "not started: the batch was cancelled" : "not started");
        var error = s.StopReason is null && s.Halted ? ErrorCode.Cancelled : s.StopError;
        var detail = why.StartsWith("not started", StringComparison.Ordinal) ? why : $"not started: {why}";
        var tasks = s.Plan.Tasks.Select((t, i) => s.Slots[i].Outcome ?? new TaskOutcome(t.Id, TaskState.NotStarted, null, detail, error));
        return new BatchOutcome([.. tasks], s.Action, s.LimitStop ? ErrorCode.RateLimited : null);
    }
}
