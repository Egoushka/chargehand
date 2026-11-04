using System.Text.Json;
using Chargehand.Modules.Accounts.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Chargehand.Server;

/// <summary><c>run-approval/v1</c>. <paramref name="LimitUsd"/> is null when no run is asked about; <paramref name="Pending"/> is how many wait for the owner.</summary>
public sealed record RunApprovalBody(string ContractVersion, decimal? LimitUsd, decimal DefaultUsd, int Pending);

/// <summary><c>GET</c> and <c>PUT /v1/limits/run-approval</c> (ADR 0092), the owner's alone. <c>PUT</c> takes <c>{"usd": 5}</c> (above zero) or <c>{"usd": null}</c> to ask about no run, and answers the body of
/// <c>GET</c>. <c>POST /v1/approvals/{id}/approve</c> and <c>/decline</c> decide a run the inbox holds.</summary>
internal static class ConsoleRunApproval
{
    public const string Contract = "run-approval/v1";

    public static void Map(WebApplication app)
    {
        app.MapGet("/v1/limits/run-approval", (HttpContext ctx, RunService runs) =>
            ChargehandServer.OwnerOnly(ctx) ?? ChargehandServer.Json(Build(runs.Approvals), StatusCodes.Status200OK));

        app.MapPut("/v1/limits/run-approval", async (HttpContext ctx, RunService runs) =>
        {
            if (ChargehandServer.OwnerOnly(ctx) is { } refused)
                return refused;
            if (await ReadAsync(ctx) is not { } parsed)
                return ChargehandServer.Json(ChargehandServer.Problem("the body must be {\"usd\": 5} with a number above 0, or {\"usd\": null} to ask about no run"), StatusCodes.Status400BadRequest);
            try
            {
                runs.Approvals.Limit.Set(parsed.Usd);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return ChargehandServer.Json(ChargehandServer.Problem($"the threshold could not be written: {e.Message}"), StatusCodes.Status503ServiceUnavailable);
            }
            return ChargehandServer.Json(Build(runs.Approvals), StatusCodes.Status200OK);
        });

        // Block bodies: an expression body would convert to RequestDelegate and answer 200 with no body.
        app.MapPost("/v1/approvals/{id}/approve", (HttpContext ctx, RunService runs, string id) =>
        {
            return Decide(ctx, runs, id, approve: true);
        });
        app.MapPost("/v1/approvals/{id}/decline", (HttpContext ctx, RunService runs, string id) =>
        {
            return Decide(ctx, runs, id, approve: false);
        });
    }

    public static RunApprovalBody Build(RunApprovalGate gate) => new(Contract, gate.Limit.LimitUsd, RunApprovalGate.DefaultUsd, gate.Pending().Count);

    private static IResult Decide(HttpContext ctx, RunService runs, string id, bool approve)
    {
        if (ChargehandServer.OwnerOnly(ctx) is { } refused)
            return refused;
        return runs.Approvals.Decide(id, approve, runs.Now) is { } decided
            ? ChargehandServer.Json(new { id = decided.Id, state = decided.State }, StatusCodes.Status200OK)
            : ChargehandServer.Json(ChargehandServer.Problem("no run waits for approval under that id (it was decided already)"), StatusCodes.Status404NotFound);
    }

    private static async Task<(decimal? Usd, bool Ok)?> ReadAsync(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("usd", out var value))
                return null;
            if (value.ValueKind == JsonValueKind.Null)
                return (null, true);
            return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var usd) && usd > 0 && usd < 1_000_000m ? (usd, true) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
