using System.Text.Json;
using Chargehand.Modules.Gateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Chargehand.Server;

/// <summary>The hosts each repository's sessions were refused, and the owner's approval of one for that repository (ADR 0078, decision 4): <c>GET /v1/egress/learned</c>, and
/// <c>POST /v1/egress/learned/approve</c> and <c>/revoke</c> with <c>{"repository": "owner/name", "host": "..."}</c>. Owner only: an approval widens what every later session of the repository
/// can reach. Only a host the repository's sessions were refused can be approved (<c>404</c> otherwise), and sending either twice is safe (<c>changed: false</c>).</summary>
internal static class ConsoleLearnedHosts
{
    public static void Map(WebApplication app, Func<HttpContext, ConsoleContext> context)
    {
        // Block bodies: an expression body would convert to RequestDelegate and answer 200 with no body (see ConsoleRoutes).
        app.MapGet("/v1/egress/learned", (HttpContext ctx) =>
        {
            return ChargehandServer.OwnerOnly(ctx) ?? List(context(ctx));
        });
        app.MapPost("/v1/egress/learned/approve", async (HttpContext ctx) =>
        {
            return ChargehandServer.OwnerOnly(ctx) ?? await ChangeAsync(context(ctx), ctx, approve: true);
        });
        app.MapPost("/v1/egress/learned/revoke", async (HttpContext ctx) =>
        {
            return ChargehandServer.OwnerOnly(ctx) ?? await ChangeAsync(context(ctx), ctx, approve: false);
        });
    }

    private static IResult Json(object body, int status) => ChargehandServer.Json(body, status);

    private static IResult NoStore() =>
        Json(ChargehandServer.Problem("this server keeps no learned hosts (it runs no driven batches, or was started without a state directory)"), StatusCodes.Status409Conflict);

    private static IResult List(ConsoleContext c)
    {
        if (c.Driven?.LearnedHosts is not { } store)
            return NoStore();
        var repositories = store.Read().OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => new
        {
            repository = r.Key,
            hosts = r.Value.Select(h => new { host = h.Host, refusals = h.Refusals, batches = h.Batches, first = h.First, last = h.Last, last_batch = h.LastBatch, approved = h.Approved }),
        });
        return Json(new { repositories }, StatusCodes.Status200OK);
    }

    private static async Task<IResult> ChangeAsync(ConsoleContext c, HttpContext ctx, bool approve)
    {
        if (c.Driven?.LearnedHosts is not { } store)
            return NoStore();
        if (await BodyAsync(ctx) is not var (repository, host))
            return Json(ChargehandServer.Problem("the body must be {\"repository\": \"owner/name\", \"host\": \"...\"}"), StatusCodes.Status400BadRequest);
        var name = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (!store.Read().TryGetValue(repository, out var hosts) || !hosts.Any(h => h.Host == name))
            return HttpResults.NotFound();
        var changed = approve ? store.Approve(repository, name, c.Now) : store.Revoke(repository, name);
        return Json(new { repository, host = name, approved = approve, changed }, StatusCodes.Status200OK);
    }

    private static async Task<(string Repository, string Host)?> BodyAsync(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("repository", out var repository) || !root.TryGetProperty("host", out var host)
                || repository.ValueKind != JsonValueKind.String || host.ValueKind != JsonValueKind.String)
                return null;
            return repository.GetString() is { Length: > 0 } r && host.GetString() is { Length: > 0 } h ? (r, h) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
