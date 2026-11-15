using System.Globalization;
using System.Text.Json;
using Chargehand.Automation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Chargehand.Server;

/// <summary>What a list field runs on now; <see cref="LimitBody{T}"/> cannot hold a list.</summary>
public sealed record ListBody<T>(IReadOnlyList<T> Profile, IReadOnlyList<T>? Override, IReadOnlyList<T> Effective);

/// <summary>A repository held by the pool: <paramref name="Text"/> is the problem, or the breaker's line.</summary>
public sealed record SweepPoolHold(string Repository, string Text, DateTimeOffset Until);

/// <summary><c>sweep/v1</c>: the sweep's schedule and its last, running and next sweep.</summary>
public sealed record SweepBody(string ContractVersion, bool Configured, bool ScheduleOn, string TimeZone, SweepRunning? Running, SweepReport? Last, DateTimeOffset? Next, IReadOnlyList<SweepPoolHold> PoolHolds);

/// <summary><c>sweep-settings/v1</c>: the profile's sweep settings, the owner's overrides and what is in force.</summary>
public sealed record SweepSettingsBody(string ContractVersion, bool Configured, string TimeZone, LimitBody<bool> Enabled, ListBody<string> At, ListBody<string> Days,
    IReadOnlyDictionary<string, LimitBody<bool>> Repositories, LimitBody<decimal> SubscriptionHoldPercent);

/// <summary>The sweep's routes, owner key only. <c>POST /v1/sweep</c> starts one (or answers the running one's id), <c>GET</c> shows the last, the running and the next, and <c>GET</c> and <c>PUT /v1/sweep/settings</c> are
/// the owner's overrides, kept in <see cref="IntakeSources"/> like <see cref="ConsoleLimits"/> and read at the next tick. A <c>PUT</c> replaces all of them; a null or absent field clears its override.</summary>
internal static class ConsoleSweep
{
    public const string Contract = "sweep/v1";
    public const string SettingsContract = "sweep-settings/v1";

    public static void Map(WebApplication app, decimal? profileHoldPercent, TimeProvider clock)
    {
        app.MapPost("/v1/sweep", (HttpContext ctx) =>
            ChargehandServer.OwnerOnly(ctx) ?? (ctx.RequestServices.GetService<SweepHost>() is { } host
                ? ChargehandServer.Json(new { id = host.Start("manual") }, StatusCodes.Status202Accepted)
                : NotConfigured()));

        app.MapGet("/v1/sweep", (HttpContext ctx) =>
            ChargehandServer.OwnerOnly(ctx) ?? ChargehandServer.Json(BuildSweep(ctx.RequestServices.GetService<SweepHost>(), ctx.RequestServices.GetService<PoolBreakerCheck>(), clock), StatusCodes.Status200OK));

        app.MapGet("/v1/sweep/settings", (HttpContext ctx) =>
            ChargehandServer.OwnerOnly(ctx) ?? ChargehandServer.Json(BuildSettings(ctx.RequestServices.GetService<SweepHost>(), profileHoldPercent), StatusCodes.Status200OK));

        app.MapPut("/v1/sweep/settings", async (HttpContext ctx) =>
        {
            if (ChargehandServer.OwnerOnly(ctx) is { } refused)
                return refused;
            if (ctx.RequestServices.GetService<SweepHost>() is not { } host)
                return NotConfigured();
            IntakeSources.SweepOverrides? parsed;
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                parsed = Parse(doc.RootElement, host.Repositories.Keys);
            }
            catch (JsonException)
            {
                parsed = null;
            }
            if (parsed is null)
                return ChargehandServer.Json(ChargehandServer.Problem(
                    "the body must be {\"enabled\": true|false|null, \"at\": [\"HH:mm\"]|null, \"days\": [\"monday\"...]|null, \"repositories\": {\"owner/repo\": true|false|null}, \"subscription_hold_percent\": above 0 and up to 100|null}"),
                    StatusCodes.Status400BadRequest);
            try
            {
                host.Sources.SetSweep(parsed);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return ChargehandServer.Json(ChargehandServer.Problem($"the sweep settings could not be written: {e.Message}"), StatusCodes.Status503ServiceUnavailable);
            }
            return ChargehandServer.Json(BuildSettings(host, profileHoldPercent), StatusCodes.Status200OK);
        });
    }

    private static IResult NotConfigured() =>
        ChargehandServer.Json(ChargehandServer.Problem("the sweep is not configured (it needs automation.github and http.pool)"), StatusCodes.Status409Conflict);

    /// <returns>Null when the body is not a valid set of overrides.</returns>
    private static IntakeSources.SweepOverrides? Parse(JsonElement body, IEnumerable<string> known)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return null;
        bool? enabled = null;
        if (body.TryGetProperty("enabled", out var e) && e.ValueKind != JsonValueKind.Null)
        {
            if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return null;
            enabled = e.GetBoolean();
        }
        if (!Strings(body, "at", out var at) || at is { Count: 0 } || at?.All(t => TimeOnly.TryParseExact(t, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) == false)
            return null;
        if (!Strings(body, "days", out var names))
            return null;
        List<DayOfWeek>? days = null;
        if (names is not null)
        {
            days = [];
            foreach (var name in names)
            {
                var day = Enum.GetValues<DayOfWeek>().Cast<DayOfWeek?>().FirstOrDefault(d => string.Equals(d.ToString(), name, StringComparison.OrdinalIgnoreCase));
                if (day is null)
                    return null;
                if (!days.Contains(day.Value))
                    days.Add(day.Value);
            }
        }
        if (!Repositories(body, known, out var repositories) || !Percent(body, out var percent))
            return null;
        return new IntakeSources.SweepOverrides(enabled, at, days, repositories, percent);
    }

    private static bool Strings(JsonElement body, string name, out List<string>? values)
    {
        values = null;
        if (!body.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null)
            return true;
        if (e.ValueKind != JsonValueKind.Array)
            return false;
        values = [];
        foreach (var item in e.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return false;
            values.Add(item.GetString()!);
        }
        return true;
    }

    private static bool Repositories(JsonElement body, IEnumerable<string> known, out Dictionary<string, bool>? repositories)
    {
        repositories = null;
        if (!body.TryGetProperty("repositories", out var e) || e.ValueKind == JsonValueKind.Null)
            return true;
        if (e.ValueKind != JsonValueKind.Object)
            return false;
        var watched = known.ToHashSet(StringComparer.Ordinal);
        repositories = [];
        foreach (var property in e.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
                continue;
            if (!watched.Contains(property.Name) || property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return false;
            repositories[property.Name] = property.Value.GetBoolean();
        }
        return true;
    }

    private static bool Percent(JsonElement body, out decimal? percent)
    {
        percent = null;
        if (!body.TryGetProperty("subscription_hold_percent", out var e) || e.ValueKind == JsonValueKind.Null)
            return true;
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetDecimal(out var d) || d is <= 0 or > 100)
            return false;
        percent = d;
        return true;
    }

    public static SweepBody BuildSweep(SweepHost? host, PoolBreakerCheck? breaker, TimeProvider clock)
    {
        var holds = breaker?.Active().Select(h => new SweepPoolHold(h.Repository, h.Text, h.Until)).ToList() ?? [];
        if (host is null)
            return new(Contract, false, false, "UTC", null, null, null, holds);
        var settings = host.Effective();
        return new(Contract, true, settings.Enabled, settings.TimeZone, host.Current, host.Last, host.Next(clock.GetUtcNow()), holds);
    }

    public static SweepSettingsBody BuildSettings(SweepHost? host, decimal? profileHoldPercent)
    {
        if (host is null)
            return new(SettingsContract, false, "UTC", Of(false, null), new([], null, []), new([], null, []), new Dictionary<string, LimitBody<bool>>(), Of(profileHoldPercent ?? 0m, null));
        var o = host.Sources.Sweep;
        var p = host.Profile;
        var repositories = host.Repositories.Keys.Order(StringComparer.Ordinal).ToDictionary(r => r, r => Of(true, o.Repositories?.TryGetValue(r, out var on) == true ? on : null), StringComparer.Ordinal);
        return new(SettingsContract, true, p.TimeZone, Of(p.Enabled, o.Enabled), Lists(p.Times, o.At), Lists([.. p.RunDays.Select(Name)], o.Days?.Select(Name).ToList()), repositories, Of(profileHoldPercent ?? 0m, o.HoldPercent));
    }

    private static string Name(DayOfWeek day) => JsonNamingPolicy.SnakeCaseLower.ConvertName(day.ToString());

    private static LimitBody<T> Of<T>(T profile, T? over) where T : struct => new(profile, over, over ?? profile);

    private static ListBody<string> Lists(IReadOnlyList<string> profile, IReadOnlyList<string>? over) => new(profile, over, over ?? profile);
}
