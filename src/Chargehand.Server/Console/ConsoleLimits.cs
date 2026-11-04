using System.Text.Json;
using Chargehand.Automation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Chargehand.Server;

/// <summary>What intake runs on now. <paramref name="Profile"/> is the profile's value, <paramref name="Override"/> the owner's (null: none), <paramref name="Effective"/> the one in force.</summary>
public sealed record LimitBody<T>(T Profile, T? Override, T Effective) where T : struct;

/// <summary><c>intake-limits/v1</c>: the owner's overrides of the intake caps, and the pause switch.</summary>
public sealed record IntakeLimitsBody(string ContractVersion, bool Configured, LimitBody<int> MaxConcurrent, LimitBody<int> MaxReviewRounds, LimitBody<decimal> MaxUsdTotal, bool Paused);

/// <summary>The owner's intake caps and pause switch (<c>GET</c> and <c>PUT /v1/intake/limits</c>), kept in <see cref="IntakeSources"/> and read by intake at its next poll. The profile stays the default; a
/// <c>null</c> or absent cap in a <c>PUT</c> clears that override, and an absent <c>paused</c> is false: a <c>PUT</c> replaces all four.</summary>
internal static class ConsoleLimits
{
    public const string Contract = "intake-limits/v1";
    private const decimal MaxUsd = 100m;

    public static void Map(WebApplication app, GitHubIntake? intake)
    {
        app.MapGet("/v1/intake/limits", (HttpContext ctx) =>
            ChargehandServer.OwnerOnly(ctx) ?? ChargehandServer.Json(Build(intake), StatusCodes.Status200OK));

        app.MapPut("/v1/intake/limits", async (HttpContext ctx) =>
        {
            if (ChargehandServer.OwnerOnly(ctx) is { } refused)
                return refused;
            if (intake is null)
                return ChargehandServer.Json(ChargehandServer.Problem("GitHub intake is not configured (the profile has no automation.github)"), StatusCodes.Status409Conflict);
            IntakeSources.Limits? limits;
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                limits = Parse(doc.RootElement);
            }
            catch (JsonException)
            {
                limits = null;
            }
            if (limits is null)
                return ChargehandServer.Json(ChargehandServer.Problem(
                    $"the body must be {{\"max_concurrent\": 1-10|null, \"max_review_rounds\": 0-10|null, \"max_usd_total\": above 0 and up to {MaxUsd}|null, \"paused\": true|false}}"),
                    StatusCodes.Status400BadRequest);
            try
            {
                intake.Sources.SetLimits(limits);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return ChargehandServer.Json(ChargehandServer.Problem($"the limits could not be written: {e.Message}"), StatusCodes.Status503ServiceUnavailable);
            }
            return ChargehandServer.Json(Build(intake), StatusCodes.Status200OK);
        });
    }

    /// <returns>Null when the body is not a valid set of limits.</returns>
    private static IntakeSources.Limits? Parse(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return null;
        if (!Int(body, "max_concurrent", 1, 10, out var concurrent) || !Int(body, "max_review_rounds", 0, 10, out var rounds))
            return null;
        decimal? usd = null;
        if (body.TryGetProperty("max_usd_total", out var u) && u.ValueKind != JsonValueKind.Null)
        {
            if (u.ValueKind != JsonValueKind.Number || !u.TryGetDecimal(out var d) || d is <= 0 or > MaxUsd)
                return null;
            usd = d;
        }
        var paused = false;
        if (body.TryGetProperty("paused", out var p))
        {
            if (p.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return null;
            paused = p.GetBoolean();
        }
        return new IntakeSources.Limits(concurrent, rounds, usd, paused);
    }

    private static bool Int(JsonElement body, string name, int min, int max, out int? value)
    {
        value = null;
        if (!body.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null)
            return true;
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out var n) || n < min || n > max)
            return false;
        value = n;
        return true;
    }

    public static IntakeLimitsBody Build(GitHubIntake? intake)
    {
        var github = intake?.Profile;
        var current = intake?.Sources.Current ?? new IntakeSources.Limits();
        return new(Contract, intake is not null,
            Of(github?.MaxConcurrent ?? 0, current.MaxConcurrent),
            Of(github?.MaxReviewRounds ?? 0, current.MaxReviewRounds),
            Of(github?.MaxUsdTotal ?? 0m, current.MaxUsdTotal),
            current.Paused);
    }

    private static LimitBody<T> Of<T>(T profile, T? over) where T : struct => new(profile, over, over ?? profile);
}
