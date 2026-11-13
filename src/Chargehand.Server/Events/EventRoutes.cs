using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Chargehand.Modules.Events.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Chargehand.Server.Events;

/// <summary>The HTTP edge of the event log (ADR 0090; threads addendum of 2026-11-08): <c>/v1/events</c>, its stream, and <c>/v1/threads</c>. The owner's key holds every
/// grant; a producer's token from <c>http.events.tokens</c> holds its own and opens nothing else. Account keys and run tokens get 403 here until a grant is minted for them.</summary>
internal static class EventRoutes
{
    public const string GrantKey = "chargehand.events.grant";

    /// <summary>Whether <paramref name="path"/> is one of these routes, the only ones a producer's token opens.</summary>
    public static bool Serves(string path) =>
        path is "/v1/events" or "/v1/threads" || path.StartsWith("/v1/events/", StringComparison.Ordinal) || path.StartsWith("/v1/threads/", StringComparison.Ordinal);

    public static void Map(WebApplication app)
    {
        var events = app.MapGroup("/v1/events");
        events.AddEndpointFilter(async (ctx, next) => Grant(ctx.HttpContext) is null ? HttpResults.StatusCode(StatusCodes.Status403Forbidden) : await next(ctx));
        events.MapPost("", AppendAsync);
        events.MapGet("", QueryAsync);
        events.MapGet("stream", Stream);
        var threads = app.MapGroup("/v1/threads");
        threads.AddEndpointFilter(async (ctx, next) => Grant(ctx.HttpContext) is null ? HttpResults.StatusCode(StatusCodes.Status403Forbidden) : await next(ctx));
        threads.MapGet("", ThreadsAsync);
        threads.MapGet("{id}/card", CardAsync);
    }

    private static async Task<IResult> AppendAsync(HttpContext ctx, EventService service)
    {
        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        }
        catch (JsonException)
        {
            return Answer(new EventAnswer(400, """{"error":{"code":"not_json","message":"the body is one envelope or an array of them","index":0}}"""));
        }
        using (doc)
            return Answer(await service.AppendAsync(Grant(ctx)!, doc.RootElement, ctx.RequestAborted));
    }

    private static async Task<IResult> QueryAsync(HttpContext ctx, EventService service) =>
        Query(ctx.Request, After(ctx.Request)) is { } query
            ? Answer(await service.QueryAsync(Grant(ctx)!, query, ctx.RequestAborted))
            : BadNumber();

    /// <summary>The events after <c>Last-Event-ID</c> (or <c>after</c>), then each new one as it is stored; each message's id is its <c>seq</c>, so a client that reconnects loses
    /// and repeats none.</summary>
    private static IResult Stream(HttpContext ctx, EventService service)
    {
        if (Query(ctx.Request, After(ctx.Request)) is not { } query)
            return BadNumber();
        var grant = Grant(ctx)!;
        if (query.Problem(grant) is { } problem)
            return Answer(new EventAnswer(problem.Status, EventService.Error(problem)));
        return TypedResults.ServerSentEvents(Sse(service.StreamAsync(grant, query, ctx.RequestAborted), ctx.RequestAborted));
    }

    private static async Task<IResult> ThreadsAsync(HttpContext ctx, EventService service, string? state, string? scope, string? limit)
    {
        int? n = null;
        if (limit is not null)
        {
            if (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                return BadNumber();
            n = parsed;
        }
        return Answer(await service.ThreadsAsync(Grant(ctx)!, state, scope, n, ctx.RequestAborted));
    }

    // The id is URL-encoded in the path (Egoushka%2Fchargehand%23812); routing leaves %2F encoded, so decode it once here.
    private static async Task<IResult> CardAsync(string id, HttpContext ctx, EventService service) =>
        Answer(await service.CardAsync(Grant(ctx)!, Uri.UnescapeDataString(id), ctx.RequestAborted));

    private static async IAsyncEnumerable<SseItem<string>> Sse(IAsyncEnumerable<StoredEvent> events, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var e in events.WithCancellation(ct))
            yield return new SseItem<string>(e.Json, e.Type) { EventId = e.Seq.ToString(CultureInfo.InvariantCulture) };
    }

    private static EventQuery? Query(HttpRequest request, long? after)
    {
        var q = request.Query;
        var limit = EventQuery.DefaultLimit;
        if (after is null || (q["limit"].ToString() is { Length: > 0 } l && !int.TryParse(l, NumberStyles.None, CultureInfo.InvariantCulture, out limit)))
            return null;
        return new EventQuery(after.Value, Param(q, "type"), Param(q, "source"), Param(q, "scope"), Param(q, "subject"), limit, Param(q, "since"), Param(q, "until"), Param(q, "correlationid"), Param(q, "actor"));
    }

    /// <summary><c>Last-Event-ID</c> when the client sends one (a reconnect), else <c>after</c>, else 0; null when either is not a number.</summary>
    private static long? After(HttpRequest request)
    {
        var raw = request.Headers["Last-Event-ID"].ToString() is { Length: > 0 } header ? header : request.Query["after"].ToString();
        if (raw.Length == 0)
            return 0;
        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var after) ? after : null;
    }

    private static string? Param(IQueryCollection q, string name) => q[name].ToString() is { Length: > 0 } v ? v : null;

    private static EventGrant? Grant(HttpContext ctx) => ctx.Items.TryGetValue(GrantKey, out var g) ? g as EventGrant : null;

    private static IResult Answer(EventAnswer a) => HttpResults.Text(a.Json, "application/json", statusCode: a.Status);

    private static IResult BadNumber() =>
        Answer(new EventAnswer(400, """{"error":{"code":"bad_number","message":"'after', 'limit' and Last-Event-ID are whole numbers","index":0}}"""));
}
