using Chargehand.Driven;
using Chargehand.Modules.Accounts.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Chargehand.Server;

/// <summary>
/// The read routes of the operator console (CHARGEHAND-209), all <c>GET</c> under <c>/v1/</c>. They show what the server and the driven runner already keep; none of them writes, starts or stops anything. The owner's
/// key sees everything; an account's key sees its own runs, batches, tasks, pull requests and spend, and the routes that describe the server itself (<c>intake</c>, <c>accounts</c>, <c>system</c>) are the owner's alone (ADR 0050).
/// A run token reaches none of them (the key middleware opens three things to it). A body never carries a secret value, a key or a token: profile secrets are shown as set or not.
/// </summary>
internal static class ConsoleRoutes
{
    public static void Map(WebApplication app, ServerSettings settings, Chargehand.Modules.Ledger.Api.IRunLog log, DrivenRun? driven)
    {
        var secrets = new SecretPresence(settings.SecretPresent);
        ConsoleContext Context(HttpContext ctx) => new(settings, ctx.RequestServices.GetRequiredService<RunService>(), log, driven, secrets);
        IResult? OwnerOnly(HttpContext ctx) => ChargehandServer.OwnerOnly(ctx);

        // Block bodies where a lambda would be one call: an expression body that is a statement also converts to RequestDelegate, which discards the result and answers 200 with no body.
        app.MapGet("/v1/overview", async (HttpContext ctx) =>
        {
            return Json(await ConsoleOverview.BuildAsync(Context(ctx), PrincipalHttp.Of(ctx)!, ctx.RequestAborted));
        });

        app.MapGet("/v1/inbox", async (HttpContext ctx) =>
        {
            var c = Context(ctx);
            if (Since(ctx, c, ConsoleInbox.DefaultWindow) is not { } since)
                return Json(ChargehandServer.Problem("since must be a number of days such as 14d, or an ISO 8601 time"), StatusCodes.Status400BadRequest);
            if (Limit(ctx, 100, 500) is not { } limit)
                return Json(ChargehandServer.Problem("limit must be 1 to 500"), StatusCodes.Status400BadRequest);
            return Json(await ConsoleInbox.BuildAsync(c, PrincipalHttp.Of(ctx), since, limit, ctx.RequestAborted));
        });

        app.MapGet("/v1/batches/{id}", async (string id, HttpContext ctx) =>
        {
            if (Limit(ctx, 500, 2000, "events", 0) is not { } events)
                return Json(ChargehandServer.Problem("events must be 0 to 2000"), StatusCodes.Status400BadRequest);
            return await ConsoleBatches.BatchAsync(Context(ctx), PrincipalHttp.Of(ctx), id, events, ctx.RequestAborted) is { } body ? Json(body) : HttpResults.NotFound();
        });

        app.MapGet("/v1/batches/{id}/tasks/{task}", async (string id, string task, HttpContext ctx) =>
        {
            if (Limit(ctx, 500, 2000, "events", 0) is not { } events)
                return Json(ChargehandServer.Problem("events must be 0 to 2000"), StatusCodes.Status400BadRequest);
            return await ConsoleBatches.TaskAsync(Context(ctx), PrincipalHttp.Of(ctx), id, task, events, ctx.RequestAborted) is { } body ? Json(body) : HttpResults.NotFound();
        });

        // The end of a session's transcript (ADR 0059): the batch's own account and the owner, never stored, never cached.
        app.MapGet("/v1/batches/{id}/tasks/{task}/sessions/{session}/transcript", async (string id, string task, string session, HttpContext ctx) =>
        {
            if (Limit(ctx, Transcript.DefaultBytes, Transcript.MaxBytes, "bytes", Transcript.MinBytes) is not { } bytes)
                return Json(ChargehandServer.Problem($"bytes must be {Transcript.MinBytes} to {Transcript.MaxBytes}"), StatusCodes.Status400BadRequest);
            ctx.Response.Headers.CacheControl = "no-store";
            return await ConsoleTranscripts.BuildAsync(Context(ctx), PrincipalHttp.Of(ctx), id, task, session, bytes, ctx.RequestAborted) is { } body ? Json(body) : HttpResults.NotFound();
        });

        app.MapGet("/v1/pulls", async (HttpContext ctx) =>
        {
            var c = Context(ctx);
            var q = ctx.Request.Query;
            var state = q.TryGetValue("state", out var stateText) ? stateText.ToString() : null;
            if (state is not null && !ConsolePulls.States.Contains(state))
                return Json(ChargehandServer.Problem($"state must be one of {string.Join(", ", ConsolePulls.States)}"), StatusCodes.Status400BadRequest);
            DateTimeOffset? since = null;
            if (q.ContainsKey("since") && (since = Since(ctx, c, null)) is null)
                return Json(ChargehandServer.Problem("since must be a number of days such as 14d, or an ISO 8601 time"), StatusCodes.Status400BadRequest);
            if (q.TryGetValue("awaiting", out var awaitingText) && !bool.TryParse(awaitingText, out _))
                return Json(ChargehandServer.Problem("awaiting must be true or false"), StatusCodes.Status400BadRequest);
            if (Limit(ctx, 100, 500) is not { } limit)
                return Json(ChargehandServer.Problem("limit must be 1 to 500"), StatusCodes.Status400BadRequest);
            return Json(await ConsolePulls.BuildAsync(c, PrincipalHttp.Of(ctx), q.TryGetValue("repository", out var repository) ? repository.ToString() : null, state,
                q.TryGetValue("awaiting", out var awaiting) && bool.Parse(awaiting.ToString()), since, limit, ctx.RequestAborted));
        });

        app.MapGet("/v1/analytics", async (HttpContext ctx) =>
        {
            var c = Context(ctx);
            var q = ctx.Request.Query;
            DateTimeOffset? since = c.Now.AddDays(-30);
            if (q.ContainsKey("since") && (since = Since(ctx, c, null)) is null)
                return Json(ChargehandServer.Problem("since must be a number of days such as 30d, or an ISO 8601 time"), StatusCodes.Status400BadRequest);
            var bucket = q.TryGetValue("bucket", out var bucketText) ? bucketText.ToString() : "day";
            if (!LearningSeries.Buckets.Contains(bucket))
                return Json(ChargehandServer.Problem($"bucket must be one of {string.Join(", ", LearningSeries.Buckets)}"), StatusCodes.Status400BadRequest);
            var groupBy = q.TryGetValue("group_by", out var groupText) ? groupText.ToString() : "none";
            if (!LearningSeries.Groupings.Contains(groupBy))
                return Json(ChargehandServer.Problem($"group_by must be one of {string.Join(", ", LearningSeries.Groupings)}"), StatusCodes.Status400BadRequest);
            return Json(await ConsoleAnalytics.BuildAsync(c, PrincipalHttp.Of(ctx), since, bucket, groupBy, q.TryGetValue("account", out var account) ? account.ToString() : null, ctx.RequestAborted));
        });

        app.MapGet("/v1/repositories", async (HttpContext ctx) =>
        {
            return Json(await ConsoleRepositories.BuildAsync(Context(ctx), PrincipalHttp.Of(ctx), ctx.RequestAborted));
        });

        ConsoleActions.Map(app, Context);

        app.MapGet("/v1/intake", (HttpContext ctx) => OwnerOnly(ctx) ?? Json(ConsoleIntake.Build(Context(ctx))));

        app.MapGet("/v1/accounts", async (HttpContext ctx) =>
            OwnerOnly(ctx) ?? Json(await ConsoleAccounts.BuildAsync(Context(ctx), settings.AccountKeysFile is { } file ? new AccountKeyStore(file) : null,
                settings.AccountKeysFile is { } keysFile ? new AccountKeyUse(AccountKeyUse.FileFor(keysFile)) : null, ctx.RequestAborted)));

        app.MapGet("/v1/system", async (HttpContext ctx) =>
            OwnerOnly(ctx) ?? Json(await ConsoleSystem.BuildAsync(Context(ctx), ctx.RequestAborted)));
    }

    private static IResult Json(object body, int status = StatusCodes.Status200OK) => ChargehandServer.Json(body, status);

    /// <summary><c>since</c> as <c>14d</c> or an ISO 8601 time, as <c>GET /v1/learn</c> takes it; the default when the query leaves it out. Null: a value that is neither.</summary>
    private static DateTimeOffset? Since(HttpContext ctx, ConsoleContext c, TimeSpan? window) =>
        ctx.Request.Query.TryGetValue("since", out var text) ? LearningSeries.ParseSince(text.ToString(), c.Now) : window is { } span ? c.Now - span : null;

    private static int? Limit(HttpContext ctx, int fallback, int max, string name = "limit", int min = 1)
    {
        if (!ctx.Request.Query.TryGetValue(name, out var text))
            return fallback;
        return int.TryParse(text, out var value) && value >= min && value <= max ? value : null;
    }
}
