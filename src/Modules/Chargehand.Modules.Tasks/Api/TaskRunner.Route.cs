using System.Collections.Concurrent;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Gateway.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Core;

namespace Chargehand.Modules.Tasks.Api;

/// <summary>The pool each session runs on (ADR 0095, D-113): asked before every session, told when its provider refuses one, and charged what the sessions on it cost.</summary>
public sealed partial class TaskRunner
{
    /// <summary>The router's answer for each session by its run id, until the task's cleanup. A start that runs again (ADR 0061) reads its model from here, so it runs on the route it was given.</summary>
    private readonly ConcurrentDictionary<string, SessionRoute> _routes = new(StringComparer.Ordinal);

    /// <summary>The router's answer for a session about to start; null with no router.</summary>
    private async Task<SessionRoute?> AskRouterAsync(CancellationToken ct) => router is null ? null : await router.RouteAsync(ct);

    /// <summary>The model the session <paramref name="runId"/> runs: the route's, else the batch's.</summary>
    private string ModelOf(string runId) => _routes.TryGetValue(runId, out var routed) && routed.Model is { } model ? model : settings.Model;

    /// <summary>The subagents' model (<c>CLAUDE_CODE_SUBAGENT_MODEL</c>) when the session was routed, so a nested call never picks the CLI's default; null: not routed.</summary>
    private string? SubagentModel(string runId) => _routes.TryGetValue(runId, out var routed) && routed.Model is { } model ? SessionModel(model) : null;

    /// <summary>A session its provider refused takes its route's pool out of the router, until the pool's refill when the budget was spent, else until the next day.</summary>
    private void NoteRefusal(string runId, SessionOutcome session)
    {
        if (router is not null && session.Status == SessionStatus.RateLimited && _routes.TryGetValue(runId, out var routed) && routed.Route is { } route)
            router.Refused(route, session.Reason == GatewayRefusal.BudgetReason);
    }

    /// <summary>A refused session gets a new one, on the next pool, while the task has not moved as often as the router has pools and its caps last. The stall continuations of ADR 0044 are counted apart.</summary>
    private bool Moves(TaskRun run) =>
        router is not null && !Bare && run.Session.Status == SessionStatus.RateLimited && run.Reroutes < router.Pools && HasRoom(run.Total);

    /// <summary>The session's cost goes to its route's entry, so the run record can say what each pool paid. A session with no route (taken up again after a restart) is charged to none.</summary>
    private void Charge(TaskRun run)
    {
        if (!_routes.TryGetValue(run.RunId, out var routed) || routed.Route is not { } route)
            return;
        var cost = settings.Priced ? run.Session.CostUsd : null;
        var at = run.Routes.FindIndex(r => r.Pool == route.Pool && r.Tier == route.Tier && r.Model == route.Model);
        if (at < 0)
            run.Routes.Add(route with { Usd = cost });
        else if (cost is not null)
            run.Routes[at] = run.Routes[at] with { Usd = (run.Routes[at].Usd ?? 0) + cost };
    }

    /// <summary>What the task's run record says of its routes: the last session's, and, when the sessions ran on more than one, all of them with each one's cost.</summary>
    private (RunRoute? Route, IReadOnlyList<RunRoute>? Routes) RoutesOf(TaskRun run) =>
        (_routes.TryGetValue(run.RunId, out var last) ? last.Route : null, run.Routes.Count > 1 ? [.. run.Routes] : null);

    /// <summary>What the next session is told and starts from. A stall continues from the stalled session's bundle (ADR 0044). A session its provider refused continues from the best bundle so far, if
    /// any; with none, the new session starts as the first did, from the same seed and with the same goal.</summary>
    private void PlanContinuation(ResolvedTask task, TaskRun run, bool refused)
    {
        var session = run.Session;
        if (!refused)
        {
            run.Goal = ContinuationGoal(task.Goal, $"Continuation {run.Continuation} of {settings.Preset.MaxContinuations}. A previous session was stopped by {session.Reason.Replace('_', ' ')} after {session.Turns} turns; "
                + "its work is on this branch (the last commit, 'WIP: ...').", ReadProgress(run.OutDirectory));
            run.StartDetail = $"session container started (continuation {run.Continuation} of {settings.Preset.MaxContinuations}, previous stopped by {session.Reason})";
            run.Seed = Path.Combine(run.OutDirectory, "chargehand.bundle");
            return;
        }
        run.StartDetail = $"session container started (moved to the next budget pool, previous refused by its provider: {session.Reason})";
        if (run.Best is not { } best)
            return;
        run.Goal = ContinuationGoal(task.Goal, $"Continuation {run.Continuation}. A previous session was cut off by its model provider ({session.Reason.Replace('_', ' ')}) after {session.Turns} turns; "
            + "the work so far is on this branch.", ReadProgress(run.OutDirectory));
        run.Seed = Path.Combine(best.OutDirectory, "chargehand.bundle");
    }

    /// <summary>A CI fix session's run id, with the route the router gave it; no run id and the reason when no pool can take it, which ends the fix attempt and not the task.</summary>
    private async Task<(string? FixRun, string? Refusal)> PlaceFixAsync(CancellationToken ct)
    {
        var route = await AskRouterAsync(ct);
        if (route?.WaitUntil is { } until)
            return (null, $"no budget pool can take a session until {until:u}");
        var fixRun = RunIds.New();
        if (route is not null)
            _routes[fixRun] = route;
        return (fixRun, null);
    }

    /// <summary>No pool can take the task's next session. A first session has no work to keep: the task ends rate-limited until the router says a pool is back, and starts nothing.</summary>
    private async Task<TaskOutcome> NoSessionAsync(ResolvedTask task, TaskRun run, DateTimeOffset until)
    {
        var outcome = new TaskOutcome(task.Id, TaskState.Failed, run.RunId, $"no budget pool can take a session until {until:u}", ErrorCode.RateLimited, RetryAfter: until, Budget: true);
        await RecordAsync(task.Id, new TaskFinished(outcome));
        run.Ended = true;
        run.Span?.SetTag("chargehand.task.state", "failed");
        run.Span?.SetTag("chargehand.error.code", "rate_limited");
        return outcome;
    }

    /// <summary>No pool can take a further session: the task ends on what it has, as when the start of one fails. A task a provider refused ends as that refusal, lifting when the router says a pool is back.</summary>
    private static void NoFurtherSession(TaskRun run, DateTimeOffset until, bool refused)
    {
        run.StartFailure = $"no budget pool can take it until {until:u}";
        if (refused)
            run.Total = run.Total with { RateLimitResetsAt = until };
    }
}
