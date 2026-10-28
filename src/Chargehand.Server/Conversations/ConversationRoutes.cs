using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Chargehand.Modules.Conversations.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Chargehand.Server.Conversations;

/// <summary>The HTTP edge of the conversations module (ADR 0086): <c>/v1/conversations</c>, owner and account keys only. A driven session's run token opens runs and MCP and nothing else,
/// so it is refused here as it is by the key check; the module knows nothing of HTTP, and this maps its outcomes to statuses.</summary>
internal static class ConversationRoutes
{
    public const string Path = "/v1/conversations";

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup(Path);
        group.AddEndpointFilter(async (ctx, next) => ctx.HttpContext.Items.ContainsKey(RunScope.ItemKey) ? HttpResults.StatusCode(StatusCodes.Status403Forbidden) : await next(ctx));
        group.MapPost("", StartAsync);
        group.MapGet("{id}", Read);
        group.MapGet("{id}/events", EventsOf);
        group.MapPost("{id}/messages", SendAsync);
        group.MapPost("{id}/approvals/{call}", AnswerAsync);
        group.MapDelete("{id}", EndAsync);
    }

    private static async Task<IResult> StartAsync(HttpContext ctx, ConversationService service)
    {
        if (await Body(ctx, ConversationJson.StartRequest) is not { } request)
            return NotJson();
        var started = await service.StartAsync(PrincipalHttp.Of(ctx), request, ctx.RequestAborted);
        if (started.Problem is { } problem)
            return Failed(ctx, problem);
        ctx.Response.Headers.Location = $"{Path}/{started.Value!.Id}";
        return HttpResults.Json(started.Value, ConversationJson.View, statusCode: StatusCodes.Status201Created);
    }

    private static IResult Read(string id, HttpContext ctx, ConversationService service) =>
        service.Find(PrincipalHttp.Of(ctx), id) is { } view ? HttpResults.Json(view, ConversationJson.View) : HttpResults.NotFound();

    /// <summary>The events after <c>Last-Event-ID</c> (all, when none is sent), then each new one until the conversation is over; each carries its id, so a client that reconnects loses and repeats none.</summary>
    private static IResult EventsOf(string id, HttpContext ctx, ConversationService service) =>
        service.Events(PrincipalHttp.Of(ctx), id, BatchEventStream.After(ctx.Request), ctx.RequestAborted) is { } events
            ? TypedResults.ServerSentEvents(Sse(events))
            : HttpResults.NotFound();

    private static async Task<IResult> SendAsync(string id, HttpContext ctx, ConversationService service)
    {
        if (await Body(ctx, ConversationJson.MessageRequest) is not { } body)
            return NotJson();
        var sent = await service.SendAsync(PrincipalHttp.Of(ctx), id, body.Text, ctx.RequestAborted);
        return sent.Problem is { } problem ? Failed(ctx, problem) : HttpResults.Json(sent.Value, ConversationJson.Accepted, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> AnswerAsync(string id, string call, HttpContext ctx, ConversationService service)
    {
        if (await Body(ctx, ConversationJson.AnswerRequest) is not { } body)
            return NotJson();
        var answered = await service.AnswerAsync(PrincipalHttp.Of(ctx), id, call, body.Decision, ctx.RequestAborted);
        return answered.Problem is { } problem ? Failed(ctx, problem) : HttpResults.Json(answered.Value, ConversationJson.Answered);
    }

    private static async Task<IResult> EndAsync(string id, HttpContext ctx, ConversationService service)
    {
        var ended = await service.EndAsync(PrincipalHttp.Of(ctx), id);
        return ended.Problem is { } problem ? Failed(ctx, problem) : HttpResults.Json(ended.Value, ConversationJson.View);
    }

    private static async IAsyncEnumerable<SseItem<string>> Sse(IAsyncEnumerable<ConversationEvent> events, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var e in events.WithCancellation(ct))
            yield return new SseItem<string>(JsonSerializer.Serialize(e, ConversationJson.Event), e.Type) { EventId = e.Id.ToString(CultureInfo.InvariantCulture) };
    }

    private static async Task<T?> Body<T>(HttpContext ctx, JsonTypeInfo<T> info) where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(ctx.Request.Body, info, ctx.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IResult NotJson() => ChargehandServer.Json(ChargehandServer.Problem("the body must be a JSON object"), StatusCodes.Status400BadRequest);

    private static IResult Failed(HttpContext ctx, ConversationProblem problem) => problem.Kind switch
    {
        ProblemKind.NotFound => HttpResults.NotFound(),
        ProblemKind.Invalid => ChargehandServer.Json(new { errors = problem.Errors }, StatusCodes.Status400BadRequest),
        ProblemKind.Conflict => ChargehandServer.Json(new { errors = problem.Errors }, StatusCodes.Status409Conflict),
        ProblemKind.Refused => ChargehandServer.Refused(ctx, problem.Refusal!),
        _ => ChargehandServer.Json(new { errors = problem.Errors, conversationId = problem.ConversationId }, StatusCodes.Status502BadGateway),
    };
}
