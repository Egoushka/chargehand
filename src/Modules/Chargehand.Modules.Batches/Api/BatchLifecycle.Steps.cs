using System.Globalization;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Gateway.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Api;

/// <summary>A batch's steps between its start and its result, which the host's wiring calls in order: the tasks resolved and planned, the token key, the tasks as the journal folds them, the
/// schedule, the retro and the result.</summary>
public sealed partial class BatchLifecycle
{
    /// <summary>The batch's tasks resolved to goals: the batch's step opened, then its <c>resolve</c> step (ADR 0061, amendment of 2026-10-14). Throws when the profile has driven sessions off,
    /// so a refused request touches nothing else.</summary>
    public async Task<(IReadOnlyList<ResolvedTask> Ready, IReadOnlyList<TaskOutcome> Failed)> ResolveAsync(BatchRunContext c, BatchOptions options, TaskSourceResolver? source)
    {
        ArgumentNullException.ThrowIfNull(c);
        Task<(IReadOnlyList<ResolvedTask> Ready, IReadOnlyList<TaskOutcome> Failed)> Prepare(CancellationToken token) =>
            DrivenBatch.PrepareAsync(c.Request, options, source, token, new ContinuationSource(log));
        if (_batches is null)
            return await Prepare(c.Ct);
        await _batches.BeginAsync(c.RunId, c.Ct);
        (IReadOnlyList<ResolvedTask> Ready, IReadOnlyList<TaskOutcome> Failed)? ran = null;
        var recorded = await _batches.ResolveAsync(c.RunId, async token =>
        {
            var prepared = (ran = await Prepare(token)).Value;
            return new BatchResolved([.. prepared.Ready.Select(BatchResolved.Entry)], prepared.Failed);
        }, c.Ct);
        return ran ?? (Resumed(c.Request, recorded.Tasks), recorded.Unresolved);
    }

    /// <summary>The batch's <c>plan</c> step: the planner's proposal applied when the request or the profile asks for a plan (ADR 0049), else the tasks as they resolved; a plan of null: none ran.
    /// An applied or skipped plan is told as a <c>plan</c> event.</summary>
    /// <param name="byProfile"><c>driven.planning.enabled</c>: plan when the request does not say.</param>
    public async Task<(IReadOnlyList<ResolvedTask> Ready, BatchPlan? Plan)> PlanAsync(BatchRunContext c, IReadOnlyList<ResolvedTask> resolved, bool byProfile, IBatchPlanner? planner)
    {
        ArgumentNullException.ThrowIfNull(c);
        var asked = c.Request.Driven?.Plan;
        async Task<(IReadOnlyList<ResolvedTask> Ready, BatchPlan? Plan)> Plan(CancellationToken token) => asked ?? byProfile
            ? await ApplyPlanAsync(c.Request, resolved, c.RunId, asked is not null ? "request" : "profile", planner, token)
            : (resolved, null);
        var (ready, plan) = _batches is null ? await Plan(c.Ct) : await RecordedPlanAsync(c, Plan);
        if (plan is not null)
            c.Progress?.Invoke(RunStatus.Of(c.RunId, RunState.Running, RunEventKind.Plan) with { Detail = plan.Describe() });
        return (ready, plan);
    }

    private async Task<(IReadOnlyList<ResolvedTask> Ready, BatchPlan? Plan)> RecordedPlanAsync(BatchRunContext c, Func<CancellationToken, Task<(IReadOnlyList<ResolvedTask> Ready, BatchPlan? Plan)>> plan)
    {
        (IReadOnlyList<ResolvedTask> Ready, BatchPlan? Plan)? ran = null;
        var recorded = await _batches!.PlanAsync(c.RunId, async token =>
        {
            var planned = (ran = await plan(token)).Value;
            return new BatchPlanned([.. planned.Ready.Select(BatchResolved.Entry)], planned.Plan);
        }, c.Ct);
        return ran ?? (Resumed(c.Request, recorded.Tasks), recorded.Plan);
    }

    /// <summary>Asks the planner (ADR 0049) and applies what it says; a planner that is missing, fails or is unusable leaves the batch as the caller sent it and says so in the plan. A cancel is not caught.</summary>
    private static async Task<(IReadOnlyList<ResolvedTask> Tasks, BatchPlan Plan)> ApplyPlanAsync(RunRequest request, IReadOnlyList<ResolvedTask> tasks, string runId, string source, IBatchPlanner? planner,
        CancellationToken ct)
    {
        if (planner is null)
            return (tasks, PlanApplier.Skipped(source, "this server has no planner", null, tasks));
        try
        {
            return PlanApplier.Apply(tasks, await planner.ProposeAsync(new PlanInput(request, tasks, runId), ct), source);
        }
        catch (Exception e) when (e is PlanException or ChargehandException or HttpRequestException or IOException)
        {
            return (tasks, PlanApplier.Skipped(source, e.Message, null, tasks));
        }
    }

    /// <summary>The key the batch's egress container signs run tokens with: its key file's (<see cref="TokenKeyFile"/>), a new one when this server keeps no state.</summary>
    public string TokenKey(string runId) => _batches is null ? ModelTokens.NewKey() : TokenKeyFile.ReadOrCreate(_batches.Directory, runId);

    /// <summary>The batch's tasks as the journal folds them; null when this server keeps no state. A new batch is carried from now on with its tasks as planned. A resumed one is folded already,
    /// by <see cref="UnfinishedAsync"/>; else it is adopted as handed in (<see cref="OpenBatches.AdoptAsync"/>).</summary>
    public async Task<TaskJournal?> TasksOfAsync(BatchRunContext c, IReadOnlyList<ResolvedTask> ready, IReadOnlyList<TaskOutcome> unresolved, BatchPlan? plan)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (_batches is null)
            return null;
        if (c.Resume is { } resume)
            return _batches.TasksOf(resume.RunId) ?? await _batches.AdoptAsync(resume, c.Ct);
        return await _batches.OpenAsync(new OpenBatch(c.RunId, c.Started, c.TraceId, c.Request, [.. ready.Select(BatchResolved.Entry)], unresolved, plan, c.Actor), c.Ct);
    }

    /// <summary>A resumed batch's tasks to run, from their entries and the request's per-task limits. A task whose session may be running stays on its runner (ADR 0058); one that had not begun
    /// is placed again. An empty name stands for a state written before runners had names.</summary>
    public static IReadOnlyList<ResolvedTask> Resumed(RunRequest request, IEnumerable<TaskEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(request);
        return [.. entries.Select(entry => Resumed(request, entry))];
    }

    private static ResolvedTask Resumed(RunRequest request, TaskEntry entry)
    {
        var asked = request.Driven?.Tasks.FirstOrDefault(t => t.Id == entry.Id);
        return new ResolvedTask(entry.Id, entry.Ref, entry.Goal, asked?.MaxTurns, asked?.MaxMinutes, entry.Size ?? asked?.Size, asked?.BudgetUsd, entry.From, entry.DependsOn ?? asked?.DependsOn,
            SplitFrom: entry.SplitFrom, SizeFrom: entry.SizeFrom, Title: entry.Title, Runner: entry.Phase == TaskPhase.Pending ? null : entry.Runner ?? "",
            Acceptance: asked?.Acceptance);
    }

    /// <summary>Runs the batch's tasks under its limits (<see cref="BatchScheduler"/>, ADR 0039): each end is journaled before the next step, and told as a <c>task_finished</c> event, as is a task that
    /// never started; a hold and a rate-limit pause are told as progress, the pause kept for <see cref="RateLimitPauses"/>. While the server is stopping, an end is not journaled: the next process
    /// takes the task up.</summary>
    public Task<BatchOutcome> ScheduleAsync(BatchRunContext c, IReadOnlyList<ResolvedTask> ready, BatchLimits limits, TaskJournal? tasks, SiteRouter router, IAdmission? admission, int requestMb)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(router);
        return new BatchScheduler(router.RunAsync, admission: admission, taskRequestMb: requestMb, control: ControlOf(c.RunId), journal: tasks).RunAsync(ready, limits, c.Ct, async o =>
        {
            if (c.Detaching())
                return;
            if (o.Budget)
                c.Progress?.Invoke(RunStatus.Of(c.RunId, RunState.Running, RunEventKind.SessionProgress) with
                {
                    TaskId = o.Id,
                    Detail = $"task {o.Id}: the model gateway's budget is used up; no task starts until {(o.RetryAfter ?? GatewayRefusal.NextUtcDay(DateTimeOffset.UtcNow)).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}, and the task can be sent again then",
                });
            if (tasks is not null)
                await tasks.FinishedAsync(o);
            c.Progress?.Invoke(TaskFinished(c.RunId, o));
        }, (id, why) => c.Progress?.Invoke(RunStatus.Of(c.RunId, RunState.Running, RunEventKind.SessionProgress) with { TaskId = id, Detail = $"task {id}: {why}" }),
        async o =>
        {
            // A task that never started (a dependency failed, or an operator cancelled it) is final too: the journal says so, so a console reads it and a restart keeps it.
            if (!c.Detaching() && tasks is not null)
                await tasks.FinishedAsync(o);
            c.Progress?.Invoke(TaskFinished(c.RunId, o));
        }, (id, lifts) => Pause(c, id, lifts));
    }

    /// <summary>After the schedule, a task that stalled with no commits (<see cref="TaskOutcome.Replan"/>, issue #432) is split into a chain of smaller tasks and run, replacing its outcome
    /// with the chain's; a task the planner cannot split this way, or without a planner at all, keeps the outcome it ended with. A second schedule wave runs only the chain's tasks, on the
    /// same router so they reach the same runner.</summary>
    public async Task<(IReadOnlyList<ResolvedTask> Ready, BatchOutcome Ran)> ReplanAsync(BatchRunContext c, IReadOnlyList<ResolvedTask> ready, BatchOutcome ran, BatchLimits limits,
        TaskJournal? tasks, SiteRouter router, IAdmission? admission, int requestMb, IBatchPlanner? planner)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(ran);
        if (planner is null || !ran.Tasks.Any(o => o.Replan))
            return (ready, ran);
        List<ResolvedTask> readyNow = [.. ready];
        List<TaskOutcome> outcomes = [.. ran.Tasks];
        List<ResolvedTask> added = [];
        foreach (var stalled in ran.Tasks.Where(o => o.Replan))
        {
            if (readyNow.FirstOrDefault(r => r.Id == stalled.Id) is not { } original || await ChainOfAsync(c, original, planner) is not { } chain)
                continue;
            readyNow.Remove(original);
            readyNow.AddRange(chain);
            outcomes.Remove(stalled);
            added.AddRange(chain);
        }
        if (added.Count == 0)
            return (ready, ran);
        var wave = await ScheduleAsync(c, added, limits, tasks, router, admission, requestMb);
        outcomes.AddRange(wave.Tasks);
        return (readyNow, new BatchOutcome(outcomes, wave.Action ?? ran.Action, wave.Error ?? ran.Error));
    }

    /// <summary>The planner's split of one stalled task, applied as a chain (<see cref="PlanApplier.Replan"/>); null when the planner fails, is refused or proposes no usable split, so the
    /// task's own outcome stands.</summary>
    private static async Task<IReadOnlyList<ResolvedTask>?> ChainOfAsync(BatchRunContext c, ResolvedTask task, IBatchPlanner planner)
    {
        try
        {
            var proposal = await planner.ProposeAsync(new PlanInput(c.Request, [task], c.RunId, Replan: true), c.Ct);
            return proposal.Tasks.FirstOrDefault(t => t.Id == task.Id)?.Split is { } split ? PlanApplier.Replan(task, split) : null;
        }
        catch (Exception e) when (e is PlanException or ChargehandException or HttpRequestException or IOException)
        {
            return null;
        }
    }

    private void Pause(BatchRunContext c, string taskId, DateTimeOffset lifts)
    {
        _pauses[c.RunId] = new RateLimitPause(c.RunId, taskId, DateTimeOffset.UtcNow, lifts);
        c.Progress?.Invoke(RunStatus.Of(c.RunId, RunState.Running, RunEventKind.SessionProgress) with
        {
            TaskId = taskId,
            Detail = $"task {taskId}: the provider's usage or rate limit was hit; new starts are held until {lifts.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}",
        });
    }

    private static RunStatus TaskFinished(string runId, TaskOutcome o) =>
        RunStatus.Of(runId, RunState.Running, RunEventKind.TaskFinished) with
        {
            TaskId = o.Id,
            Branch = o.Branch,
            PrUrl = o.PrUrl,
            Tokens = o.Tokens,
            Detail = $"task {o.Id}: {JsonNamingPolicy.SnakeCaseLower.ConvertName(o.State.ToString())}{(o.Detail is { } d ? $": {d}" : "")}",
            Runner = o.Runner,
        };

    /// <summary>The retro after a batch (ADR 0045); null while retros are off, and while the server stops or the caller cancels. One that cannot be written is reported skipped with the reason,
    /// and never changes the batch's result. A cancel is not caught.</summary>
    /// <param name="on"><c>driven.learning.retro</c>.</param>
    public static async Task<BatchRetro?> RetroAsync(BatchRunContext c, bool on, ILearningStore? learning, IBatchRetro? writer, IRetroSink? sink)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.Detaching() || c.Ct.IsCancellationRequested || !on)
            return null;
        if (learning is null || writer is null)
            return BatchRetro.Skipped("this server keeps no learning records or has no retro writer");
        var records = (await learning.ReadAsync(c.Ct, includeBench: true)).Where(e => e.Record.BatchId == c.RunId).Select(e => e.Record).ToList();
        if (records.Count == 0)
            return BatchRetro.Skipped("no task of the batch left a learning record");
        BatchRetro retro;
        try
        {
            retro = await writer.WriteAsync(new RetroInput(c.Request, c.RunId, records), c.Ct);
        }
        catch (Exception e) when (e is RetroException or ChargehandException or HttpRequestException or IOException)
        {
            return BatchRetro.Skipped(e.Message, records.Count);
        }
        await PublishAsync(c.RunId, retro, sink, c.Ct);
        return retro;
    }

    private static async Task PublishAsync(string runId, BatchRetro retro, IRetroSink? sink, CancellationToken ct)
    {
        if (sink is null)
            return;
        try
        {
            await sink.PublishAsync(runId, retro, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine($"chargehand: the retro sink failed for batch {runId}: {e.Message}");
        }
    }

    /// <summary>The batch's result: in request order, a task the plan split in place; tasks that did not resolve were never started.</summary>
    /// <param name="delivery">How the model credential reached the sessions, for the result.</param>
    public static ResultContract Result(BatchRunContext c, IReadOnlyList<TaskOutcome> unresolved, IReadOnlyList<ResolvedTask> ready, BatchOutcome ran, string delivery, bool priced, string claudeVersion,
        BatchPlan? plan, BatchRetro? retro, BatchFallback? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(ran);
        List<TaskOutcome> all = [];
        foreach (var t in c.Request.Driven!.Tasks)
            all.AddRange(unresolved.FirstOrDefault(u => u.Id == t.Id) is { } none
                ? [none]
                : ready.Where(r => r.Id == t.Id || r.SplitFrom == t.Id).Select(r => ran.Tasks.First(o => o.Id == r.Id)));
        c.Span?.SetTag("chargehand.driven.pr_urls", all.Select(o => o.PrUrl).OfType<string>().ToArray());
        c.Span?.SetTag("chargehand.driven.tokens", all.Sum(o => o.Tokens));
        return DrivenResult.BuildBatch(c.RunId, c.TraceId, "", new BatchOutcome(all, ran.Action, ran.Error), delivery, priced, claudeVersion,
            cancelledByCaller: c.Ct.IsCancellationRequested && c.CancelledByCaller(), plan, retro, fallback);
    }
}
