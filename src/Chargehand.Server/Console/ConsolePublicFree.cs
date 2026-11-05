using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Chargehand.Server;

/// <summary><c>public-free/v1</c>. <paramref name="Configured"/> is whether the profile has a <c>public_free</c> block; <paramref name="Preset"/> is the pool's preset, null when it has none.</summary>
public sealed record PublicFreeBody(string ContractVersion, bool Enabled, bool Configured, string? Preset);

/// <summary><c>GET</c> and <c>PUT /v1/limits/public-free</c> (ADR 0093), the owner's alone. <c>PUT</c> takes <c>{"enabled": true}</c> or <c>{"enabled": false}</c> and answers the body of
/// <c>GET</c>. Off is the default; off also refuses a request that names the pool preset.</summary>
internal static class ConsolePublicFree
{
    public const string Contract = "public-free/v1";

    public static void Map(WebApplication app)
    {
        app.MapGet("/v1/limits/public-free", (HttpContext ctx, RunService runs) =>
            ChargehandServer.OwnerOnly(ctx) ?? ChargehandServer.Json(Build(runs), StatusCodes.Status200OK));

        app.MapPut("/v1/limits/public-free", async (HttpContext ctx, RunService runs) =>
        {
            if (ChargehandServer.OwnerOnly(ctx) is { } refused)
                return refused;
            if (runs.PublicFree is not { } gate)
                return ChargehandServer.Json(ChargehandServer.Problem("the profile has no public_free block, so there is no pool to switch"), StatusCodes.Status409Conflict);
            if (await ReadAsync(ctx) is not { } enabled)
                return ChargehandServer.Json(ChargehandServer.Problem("the body must be {\"enabled\": true} or {\"enabled\": false}"), StatusCodes.Status400BadRequest);
            try
            {
                gate.Pool.Switch.Set(enabled);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return ChargehandServer.Json(ChargehandServer.Problem($"the setting could not be written: {e.Message}"), StatusCodes.Status503ServiceUnavailable);
            }
            return ChargehandServer.Json(Build(runs), StatusCodes.Status200OK);
        });
    }

    public static PublicFreeBody Build(RunService runs) =>
        new(Contract, runs.PublicFree?.Pool.Enabled ?? false, runs.PublicFree is not null, runs.PublicFree?.Preset);

    private static async Task<bool?> ReadAsync(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("enabled", out var value))
                return null;
            return value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
