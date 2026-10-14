using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Batches.Core.Scheduling;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Api;

/// <summary>What the shell runs a batch's tasks with: the runner delegate, the shared slots, the host, the operator's controls, the clock and the batch's journal (null: nothing is kept).</summary>
internal sealed record ScheduleHost(RunTask Run, SemaphoreSlim? Global, IAdmission? Admission, int TaskRequestMb, BatchControl? Control, TimeProvider Time, TaskJournal? Journal = null);

/// <summary>The caller's callbacks; see <see cref="BatchScheduler.RunAsync"/>.</summary>
internal sealed record ScheduleCallbacks(Func<TaskOutcome, Task>? Finished, Action<string, string>? Hold, Func<TaskOutcome, Task>? Skipped, Action<string, DateTimeOffset>? Pause);

/// <summary>One batch's run of <see cref="BatchMachine"/>: it carries out each step the machine asks for and feeds back what happened as events, with the time read on the host's clock.
/// The state is swapped under one lock, because live usage arrives on the tasks' own threads; a cancel the machine asks for happens under it too, so it never reaches a task that ended. A task's end
/// and skip are told to the caller, and waited for, before the next step: what the caller records of them is in place before another task starts. With a journal (ADR 0063), each end and each
/// operator cancel of a task that has not started is appended before that, and a restart folds them again before its first look at the batch; nothing decided after a halt is kept.</summary>
internal sealed class BatchSchedulerRun(SchedulePlan plan, ScheduleHost host, ScheduleCallbacks on, CancellationToken ct) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<int, LiveTask> _live = [];
    private readonly SemaphoreSlim _changed = new(0);
    private readonly Queue<Func<Task>> _told = [];
    private ScheduleState _state = BatchMachine.Begin(plan);

    private sealed class LiveTask(string? placedOn, CancellationTokenSource cts)
    {
        public string? PlacedOn { get; } = placedOn;
        public CancellationTokenSource Cts { get; } = cts;
        public Task<TaskOutcome>? Task { get; set; }
    }

    public async Task<BatchOutcome> RunAsync()
    {
        await ResumeAsync();
        var step = await SweepAsync();
        while (step is not Done)
            step = await NextAsync(step);
        return ((Done)step).Outcome;
    }

    public void Dispose()
    {
        host.Control?.Keep(null);
        _changed.Dispose();
    }

    // A restart takes the batch up where its journal left it: ends, placements, the pause and operator cancels are in place before the first look, and a pause that holds still is told again.
    private async Task ResumeAsync()
    {
        if (host.Journal is not { } journal)
            return;
        var (state, pause) = BatchMachine.Replay(plan, journal.Others.Select(e => ScheduleEventJson.Decode(e.Kind, e.Payload)).OfType<ScheduleEvent>());
        lock (_gate)
            _state = state;
        if (pause is not null && pause.Until > Now)
            on.Pause?.Invoke(pause.TaskId, pause.Until);
        foreach (var id in host.Control?.Keep(KeepCancelAsync) ?? [])
            await KeepCancelAsync(id);
    }

    private Task KeepCancelAsync(string taskId)
    {
        var (kind, payload) = ScheduleEventJson.Encode(taskId, Now);
        return host.Journal!.AppendAsync(kind, payload);
    }

    private async Task<ScheduleStep> NextAsync(ScheduleStep step)
    {
        switch (step)
        {
            case Place place:
                var placed = await PlaceAsync(place);
                await TellAsync();
                return placed;
            case AwaitTasks:
                await AnyChangeAsync();
                await EndCompletedAsync();
                break;
            case WaitUntil wait:
                // A usage or rate limit holds new starts: wait until it lifts, or for a halt.
                if (wait.Until - Now is { Ticks: > 0 } left)
                    await Task.Delay(left, host.Time, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                break;
            default:
                // Only the shared limit or the host is in the way: look again shortly.
                await Task.Delay(100, CancellationToken.None);
                break;
        }
        return await SweepAsync();
    }

    private DateTimeOffset Now => host.Time.GetUtcNow();

    // A task an operator cancelled before it started never does: the cancels reach the machine before it looks at the batch.
    private async Task<ScheduleStep> SweepAsync()
    {
        if (host.Control is { } control)
        {
            ScheduleState state;
            lock (_gate)
                state = _state;
            foreach (var id in BatchMachine.Pending(state).Where(control.IsCancelled).ToList())
                Apply(new OperatorCancelled(id));
        }
        var step = Apply(new Tick(Now))!;
        await TellAsync();
        return step;
    }

    private async Task<ScheduleStep> PlaceAsync(Place place)
    {
        if (host.Global is not null && !await host.Global.WaitAsync(0, CancellationToken.None))
            return Apply(new Blocked(Now))!;
        if (host.Admission is null)
            return Apply(new Admitted(place.TaskId, null, null, Now))!;
        AdmissionDecision decision;
        try
        {
            decision = await host.Admission.TryAdmitAsync(host.TaskRequestMb, place.Runner, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            host.Global?.Release();
            return Apply(new Blocked(Now))!;
        }
        if (decision.Admit)
            return Apply(new Admitted(place.TaskId, decision.Runner, decision.Detail, Now))!;
        host.Global?.Release();
        // Refused: nothing will place it, so the batch stops starting tasks and what runs finishes.
        return decision.Refuse ? Apply(new Refused(place.TaskId, decision.Detail, Now))! : Apply(new Held(place.TaskId, decision.Detail!, Now))!;
    }

    // Wait for a task to finish or report.
    private async Task AnyChangeAsync()
    {
        Task<TaskOutcome>[] running;
        lock (_gate)
            running = [.. _live.Values.Select(l => l.Task!)];
        await Task.WhenAny(Task.WhenAny(running), _changed.WaitAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None));
    }

    private async Task EndCompletedAsync()
    {
        List<KeyValuePair<int, LiveTask>> live;
        lock (_gate)
            live = [.. _live];
        foreach (var (index, task) in live)
        {
            if (!task.Task!.IsCompleted)
                continue;
            var outcome = await task.Task;
            host.Control?.Ended(outcome.Id);
            lock (_gate)
                _live.Remove(index);
            host.Global?.Release();
            host.Admission?.Ended(task.PlacedOn);
            task.Cts.Dispose();
            Apply(new Ended(plan.Tasks[index].Id, outcome, Now));
            await TellAsync();
        }
    }

    /// <summary>The ends and skips the last decisions asked for, told to the caller one at a time, in order.</summary>
    private async Task TellAsync()
    {
        while (true)
        {
            Func<Task>? next;
            lock (_gate)
                if (!_told.TryDequeue(out next))
                    return;
            await next();
        }
    }

    private void Tell(Func<TaskOutcome, Task>? callback, TaskOutcome outcome)
    {
        if (callback is null)
            return;
        lock (_gate)
            _told.Enqueue(() => callback(outcome));
    }

    private ScheduleStep? Apply(ScheduleEvent input)
    {
        ScheduleDecision decision;
        lock (_gate)
        {
            // The halt is read as each decision is made, as a cancelled token reads before its callbacks run: a task that ends on the halt never frees its slot for another.
            if (ct.IsCancellationRequested && !_state.Halted)
                _state = BatchMachine.Decide(_state, new Halt()).State;
            decision = BatchMachine.Decide(_state, input);
            if (input is Ended ended && !_state.Halted)
                Keep(ended, decision);
            _state = decision.State;
            foreach (var cancel in decision.Commands.OfType<Cancel>())
                if (_live.TryGetValue(plan.IndexOf[cancel.TaskId], out var running))
                    running.Cts.Cancel();
        }
        foreach (var command in decision.Commands)
            Execute(command);
        return decision.Step;
    }

    // The end goes to the journal before the caller is told of it. A final end is kept as the caps left it: what rewrote it (live usage) is not journaled, so a replay must not rewrite it again.
    private void Keep(Ended ended, ScheduleDecision decision)
    {
        if (host.Journal is not { } journal)
            return;
        var kept = ended.Outcome.Replace ? ended : ended with { Outcome = decision.Commands.OfType<Finish>().First().Outcome };
        var (kind, payload) = ScheduleEventJson.Encode(kept);
        _told.Enqueue(() => journal.AppendAsync(kind, payload));
    }

    private void Execute(ScheduleCommand command)
    {
        switch (command)
        {
            case Start start:
                StartTask(start);
                break;
            case Skip skip:
                Tell(on.Skipped, skip.Outcome);
                break;
            case Finish finish:
                Tell(on.Finished, finish.Outcome);
                break;
            case Hold hold:
                on.Hold?.Invoke(hold.TaskId, hold.Why);
                break;
            case Pause pause:
                on.Pause?.Invoke(pause.TaskId, pause.Until);
                break;
        }
    }

    private void StartTask(Start start)
    {
        var live = new LiveTask(start.PlacedOn, CancellationTokenSource.CreateLinkedTokenSource(ct));
        lock (_gate)
            _live[plan.IndexOf[start.TaskId]] = live;
        host.Control?.Started(start.TaskId, live.Cts);
        var progress = new SyncProgress(u =>
        {
            Apply(new Reported(start.TaskId, u.Tokens, u.Usd));
            _changed.Release();
        });
        // Called directly, not on the thread pool: it runs up to its first await here, so tasks begin in order.
        live.Task = ExecuteAsync(start.Task, progress, live.Cts);
    }

    private async Task<TaskOutcome> ExecuteAsync(ResolvedTask task, IProgress<TaskUsage> progress, CancellationTokenSource cts)
    {
        try
        {
            return await host.Run(task, progress, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return new TaskOutcome(task.Id, TaskState.Cancelled, null, "cancelled", ErrorCode.Cancelled);
        }
        catch (Exception e)
        {
            var error = ChargehandException.ErrorOf(e);
            return new TaskOutcome(task.Id, TaskState.Failed, null, error.Message, error.Code);
        }
    }

    /// <summary>Calls back on the reporting thread; unlike <see cref="Progress{T}"/> it does not post to a captured context.</summary>
    private sealed class SyncProgress(Action<TaskUsage> report) : IProgress<TaskUsage>
    {
        public void Report(TaskUsage value) => report(value);
    }
}
