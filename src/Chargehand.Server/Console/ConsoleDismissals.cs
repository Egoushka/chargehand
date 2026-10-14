using System.Text.Json;
using Chargehand.Modules.Accounts.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Chargehand.Server;

/// <summary>Dismissing inbox items and undoing it (ADR 0064): <c>POST /v1/inbox/dismiss</c> and <c>POST /v1/inbox/restore</c>, each with <c>{"ids": [...]}</c>, the ids as the inbox shows them (a pull
/// request's holds its URL, so the ids travel in the body, not the path). For the owner and for an account, each for the items its own inbox shows; an id the caller's inbox does not hold is
/// <c>unknown</c>, and a request with nothing but unknown ids is a <c>404</c>. Safe to send twice: an item dismissed already reads <c>changed: false</c>.</summary>
internal static class ConsoleDismissals
{
    public const int MaxIds = 500;

    public static void Map(WebApplication app, Func<HttpContext, ConsoleContext> context)
    {
        // Block bodies: an expression body would convert to RequestDelegate and answer 200 with no body (see ConsoleRoutes).
        app.MapPost("/v1/inbox/dismiss", async (HttpContext ctx) =>
        {
            return await ChangeAsync(context(ctx), ctx, dismiss: true);
        });
        app.MapPost("/v1/inbox/restore", async (HttpContext ctx) =>
        {
            return await ChangeAsync(context(ctx), ctx, dismiss: false);
        });
    }

    private static IResult Json(object body, int status) => ChargehandServer.Json(body, status);

    private static async Task<IResult> ChangeAsync(ConsoleContext c, HttpContext ctx, bool dismiss)
    {
        if (c.Dismissals is not { } store)
            return Json(ChargehandServer.Problem("this server keeps no inbox dismissals (it was started without a state directory)"), StatusCodes.Status409Conflict);
        if (await IdsAsync(ctx) is not { } ids)
            return Json(ChargehandServer.Problem($"the body must be {{\"ids\": [...]}} with 1 to {MaxIds} item ids"), StatusCodes.Status400BadRequest);
        var who = PrincipalHttp.Of(ctx);
        // Every item the caller could list with any window: a dismissal outlives the default 14 days.
        var view = await ConsoleInbox.ViewAsync(c, who, DateTimeOffset.MinValue, ctx.RequestAborted);
        // Two entries can share an id (a task that failed twice with different codes): an id dismisses both.
        var open = view.Open.GroupBy(e => e.Item.Id).ToDictionary(g => g.Key, g => g.SelectMany(e => e.Members).Distinct().ToList());
        List<string> changed = [], unchanged = [], unknown = [];
        try
        {
            foreach (var id in ids)
            {
                if (dismiss && open.TryGetValue(id, out var members))
                    (store.Add(members, id, who is { IsOwner: false } ? who.Name : null, c.Now).Count > 0 ? changed : unchanged).Add(id);
                else if (dismiss && view.Closed[id].Contains(ConsoleInbox.Dismissed))
                    unchanged.Add(id);
                else if (!dismiss && (view.Closed.Contains(id) || open.ContainsKey(id)))
                    (store.Remove(id).Count > 0 ? changed : unchanged).Add(id);
                else
                    unknown.Add(id);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Json(ChargehandServer.Problem($"the dismissals could not be written: {e.Message}"), StatusCodes.Status503ServiceUnavailable);
        }
        if (changed.Count == 0 && unchanged.Count == 0)
            return HttpResults.NotFound();
        return Json(new { action = dismiss ? "dismiss" : "restore", changed = changed.Count > 0, ids = changed, unchanged, unknown }, StatusCodes.Status200OK);
    }

    private static async Task<IReadOnlyList<string>?> IdsAsync(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Array)
                return null;
            var list = ids.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() : null).ToList();
            return list.Count is > 0 and <= MaxIds && list.All(i => !string.IsNullOrEmpty(i)) ? [.. list.Distinct(StringComparer.Ordinal).Select(i => i!)] : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
