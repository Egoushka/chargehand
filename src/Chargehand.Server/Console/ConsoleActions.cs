using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Forge.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Chargehand.Server;

/// <summary>What the console can do to one task or pull request (CHARGEHAND-214): cancel a task, retry it, stop its session, re-run a pull request's failed required checks. Each is a <c>POST</c> for the
/// owner and for the account that started the batch (the same test as every batch route: another account gets <c>404</c>); a run token reaches none. Each writes an <c>operator</c> event to the batch,
/// is safe to send twice, and answers <c>409</c> when the state forbids it. The body of every answer is small and holds no secret.</summary>
internal static class ConsoleActions
{
    public static void Map(WebApplication app, Func<HttpContext, ConsoleContext> context)
    {
        app.MapPost("/v1/batches/{id}/tasks/{task}/cancel", async (string id, string task, HttpContext ctx) =>
            await CancelAsync(context(ctx), PrincipalHttp.Of(ctx), id, task, ctx.RequestAborted));

        app.MapPost("/v1/batches/{id}/tasks/{task}/session/stop", async (string id, string task, HttpContext ctx) =>
            await StopSessionAsync(context(ctx), PrincipalHttp.Of(ctx), id, task, ctx.RequestAborted));

        var retries = new Retries();
        app.MapPost("/v1/batches/{id}/tasks/{task}/retry", async (string id, string task, HttpContext ctx) =>
            await RetryAsync(context(ctx), retries, PrincipalHttp.Of(ctx), id, task, ctx));

        app.MapPost("/v1/pulls/{owner}/{repo}/{number:int}/checks/rerun", async (string owner, string repo, int number, HttpContext ctx) =>
            await RerunChecksAsync(context(ctx), PrincipalHttp.Of(ctx), owner, repo, number, ctx.RequestAborted));
    }

    private static IResult Json(object body, int status) => ChargehandServer.Json(body, status);

    private static IResult Conflict(string why) => Json(ChargehandServer.Problem(why), StatusCodes.Status409Conflict);

    private static void Tell(ConsoleContext c, Principal? who, string batch, string? task, string text) =>
        c.Driven?.Announce(batch, RunStatus.Of(batch, RunState.Running, RunEventKind.Operator) with { TaskId = task, Detail = task is null ? text : $"task {task}: {text}", Actor = who?.Name });

    private static async Task<IResult> CancelAsync(ConsoleContext c, Principal? who, string batch, string task, CancellationToken ct)
    {
        if (await ConsoleBatches.TaskAsync(c, who, batch, task, 0, ct) is not { } detail)
            return HttpResults.NotFound();
        var state = detail.Task.State;
        if (state == "cancelled")
            return Json(new { batch_run_id = batch, task_id = task, action = "cancel", state, changed = false }, StatusCodes.Status200OK);
        if (state is not ("pending" or "running"))
            return Conflict($"task {task} has ended ({state.Replace('_', ' ')}); there is nothing to cancel");
        if (c.Driven?.ControlOf(batch) is not { } control)
            return Conflict("this server does not run that batch (it has finished, or another process runs it)");
        if (control.IsCancelled(task))
            return Json(new { batch_run_id = batch, task_id = task, action = "cancel", state = "cancelling", changed = false }, StatusCodes.Status202Accepted);
        // A task that has not started is live while the saved state shows it pending with no outcome.
        bool Pending(string id) => c.Driven.LiveBatches().FirstOrDefault(b => b.RunId == batch)?.Tasks.FirstOrDefault(t => t.Id == id) is { Phase: TaskPhase.Pending, Outcome: null };
        if (!control.CancelTask(task, Pending))
            return Conflict($"task {task} is no longer running");
        Tell(c, who, batch, task, "cancelled by an operator");
        return Json(new { batch_run_id = batch, task_id = task, action = "cancel", state = "cancelling", changed = true }, StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> StopSessionAsync(ConsoleContext c, Principal? who, string batch, string task, CancellationToken ct)
    {
        if (await ConsoleBatches.TaskAsync(c, who, batch, task, 0, ct) is not { } detail)
            return HttpResults.NotFound();
        if (detail.Task.State != "running")
            return Conflict($"task {task} is {detail.Task.State.Replace('_', ' ')}, so it has no session to stop");
        if (c.Driven?.ControlOf(batch) is not { } control)
            return Conflict("this server does not run that batch (it has finished, or another process runs it)");
        if (!await control.StopSessionAsync(task))
            return Conflict($"task {task} has no session running right now");
        Tell(c, who, batch, task, "session stopped by an operator");
        return Json(new { batch_run_id = batch, task_id = task, action = "session_stop", changed = true }, StatusCodes.Status202Accepted);
    }

    /// <summary>A new batch of one task, started by the caller: the task as the batch was asked, or, if the failed task kept work, a continuation of it (<c>continue_from</c>, ADR 0045). Only for a batch that
    /// has finished and a task that failed, was cancelled or never started; a task that waits for an answer is answered instead. The task's <c>depends_on</c> is dropped, because what it depended on is in
    /// the old batch. A second retry of the same task returns the batch the first one started.</summary>
    /// <summary>The retries this process started, by marker: a retry's start record reaches the run log after its <c>202</c>, so a second retry sent right after the first would not find it there.
    /// One retry is decided at a time, so two sent together start one batch.</summary>
    private sealed class Retries
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<string, (string RunId, string? Actor)> Started { get; } = [];
    }

    private static async Task<IResult> RetryAsync(ConsoleContext c, Retries retries, Principal? who, string batch, string task, HttpContext http)
    {
        await retries.Gate.WaitAsync(http.RequestAborted);
        try
        {
            return await RetryOnceAsync(c, retries, who, batch, task, http);
        }
        finally
        {
            retries.Gate.Release();
        }
    }

    private static async Task<IResult> RetryOnceAsync(ConsoleContext c, Retries retries, Principal? who, string batch, string task, HttpContext http)
    {
        var ct = http.RequestAborted;
        var runs = c.Runs;
        if (await ConsoleBatches.TaskAsync(c, who, batch, task, 0, ct) is not { } detail)
            return HttpResults.NotFound();
        var index = await c.IndexAsync(ct);
        var marker = $"Retry of {batch}/{task}.";
        var earlier = retries.Started.TryGetValue(marker, out var held) && Principal.Sees(who, held.Actor) ? held.RunId
            : index.Batches.FirstOrDefault(s => s.Request.Text == marker && Principal.Sees(who, s.Actor))?.RunId;
        if (earlier is not null)
            return Json(new { batch_run_id = batch, task_id = task, action = "retry", run_id = earlier, changed = false }, StatusCodes.Status200OK);
        if (index.RunOf(batch) is null)
            return Conflict("the batch is still running; cancel the task and retry it once the batch has finished");
        if (detail.Task.State is not ("failed" or "cancelled" or "not_started"))
            return Conflict(detail.Task.State == "needs_input" ? $"task {task} is waiting for an answer: answer it instead" : $"task {task} {detail.Task.State.Replace('_', ' ')}; only a failed, cancelled or unstarted task is retried");
        if (!index.Starts.TryGetValue(batch, out var start) || start.Request.Driven?.Tasks.FirstOrDefault(t => t.Id == task) is not { } asked)
            return Conflict("the batch's request is not in the run log");
        if (runs.Halted)
            return Json(ChargehandServer.Problem(RunService.HaltedMessage), StatusCodes.Status503ServiceUnavailable);
        var kept = detail.KeptWork is not null && detail.Task.RunId is not null;
        var again = kept ? asked with { Goal = null, Ref = null, DependsOn = null, ContinueFrom = new ContinueFrom(batch, task), Answers = null } : asked with { DependsOn = null };
        var request = start.Request with { Text = marker, Driven = start.Request.Driven with { Tasks = [again], MaxParallel = 1 } };
        var admitted = await runs.AdmitAsync(who, request, ct);
        if (admitted.Refusal is { } refusal)
            return Json(refusal.Body(), refusal.Status);
        if (runs.Start(admitted.Request, null, null, who?.Name) is not { } run)
            return Json(ChargehandServer.Problem($"{RunService.MaxUnfinished} runs are unfinished; retry later"), StatusCodes.Status429TooManyRequests);
        retries.Started[marker] = (run.Id, who?.Name);
        http.Response.Headers.Location = $"/v1/runs/{run.Id}";
        Tell(c, who, batch, task, $"retry started as run {run.Id}{(kept ? ", continuing the work it kept" : "")}");
        return Json(new { batch_run_id = batch, task_id = task, action = "retry", run_id = run.Id, continue_from = kept, changed = true }, StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> RerunChecksAsync(ConsoleContext c, Principal? who, string owner, string repo, int number, CancellationToken ct)
    {
        var entry = (await c.LearningAsync(ct)).Where(e => e.Record.Pr is { } pr && pr.Number == number && pr.Remote.Owner == owner && pr.Remote.Name == repo && Principal.Sees(who, e.Record.Actor))
            .OrderBy(e => e.Record.At).LastOrDefault();
        if (entry?.Record.Pr is not { } pull)
            return HttpResults.NotFound();
        if (c.Driven?.ReadHost(pull.Remote) is not { } host)
            return Conflict("this server has no credential for that pull request's forge");
        try
        {
            var snapshot = await host.GetAsync(pull.Remote, number, ct);
            if (snapshot.Merged || snapshot.Closed)
                return Conflict($"the pull request is {(snapshot.Merged ? "merged" : "closed")}");
            var failed = snapshot.Checks.Where(k => k is { Required: true, Outcome: CheckOutcome.Failed, JobId: not null }).ToList();
            if (failed.Count == 0)
                return Conflict("no required check has failed (or the failed ones are already running again)");
            var rerun = await host.RerunFailedJobsAsync(pull.Remote, [.. failed.Select(k => k.JobId!.Value)], ct);
            var names = failed.Select(k => k.Name).Distinct().Order(StringComparer.Ordinal).ToList();
            Tell(c, who, entry.Record.BatchId, entry.Record.TaskId, $"failed checks re-run for pull request #{number}: {string.Join(", ", names)}");
            return Json(new { pull = pull.Url, number, action = "checks_rerun", rerun_runs = rerun, checks = names, changed = true }, StatusCodes.Status202Accepted);
        }
        catch (IntegrationException e)
        {
            return Json(ChargehandServer.Problem($"the forge refused: {e.Message}"), StatusCodes.Status502BadGateway);
        }
    }
}
