using System.Globalization;
using System.Text.Json;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Ledger.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Chargehand.Server;

/// <summary><c>daily-warning/v1</c>. <paramref name="LimitUsd"/> is null when the warning is off; <paramref name="SpentUsd"/> is what API keys were billed across the owner and every account in the UTC day.</summary>
public sealed record DailyWarningBody(string ContractVersion, decimal? LimitUsd, decimal DefaultUsd, decimal SpentUsd, bool Exceeded, DateTimeOffset ResetsAt);

/// <summary>The server-wide soft daily dollar limit (ADR 0091), kept in <c>limits/daily-warning.json</c> beside the run log and written whole through a temporary file. It never refuses a run: it only
/// raises a warning. The account caps of ADR 0050 stay hard.</summary>
public sealed class DailyWarningLimit
{
    public const decimal DefaultUsd = 15m;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    private readonly string? _path;
    private readonly Lock _lock = new();
    private decimal? _limit = DefaultUsd;
    private DateOnly? _warnedDay;

    public DailyWarningLimit(string? path)
    {
        _path = path;
        if (path is not null && File.Exists(path) && JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json) is { } stored)
            _limit = stored.LimitUsd;
    }

    private sealed record Stored(decimal? LimitUsd);

    public decimal? LimitUsd
    {
        get
        {
            lock (_lock)
                return _limit;
        }
    }

    public void Set(decimal? usd)
    {
        lock (_lock)
        {
            _limit = usd;
            if (_path is null)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temp = $"{_path}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Stored(usd), Json));
            File.Move(temp, _path, overwrite: true);
        }
    }

    /// <summary>True once per UTC day, the first time the spend is seen at or past the limit; the caller logs it.</summary>
    public bool FirstCrossing(DateTimeOffset now, decimal spent)
    {
        lock (_lock)
        {
            var day = DateOnly.FromDateTime(now.UtcDateTime);
            if (_limit is not { } cap || spent < cap || _warnedDay == day)
                return false;
            _warnedDay = day;
            return true;
        }
    }
}

/// <summary><c>GET</c> and <c>PUT /v1/limits/daily-warning</c> (ADR 0091), the owner's alone. <c>PUT</c> takes <c>{"usd": 20}</c> (above zero) or <c>{"usd": null}</c> to switch the warning off, and answers the body of <c>GET</c>.</summary>
internal static class ConsoleDailyWarning
{
    public const string Contract = "daily-warning/v1";

    public static void Map(WebApplication app, ServerSettings settings, IRunLog log, DailyWarningLimit limit)
    {
        var clock = settings.Clock ?? TimeProvider.System;
        app.MapGet("/v1/limits/daily-warning", async (HttpContext ctx) =>
            ChargehandServer.OwnerOnly(ctx) ?? ChargehandServer.Json(await BuildAsync(settings, log, limit, clock.GetUtcNow(), ctx.RequestAborted), StatusCodes.Status200OK));

        app.MapPut("/v1/limits/daily-warning", async (HttpContext ctx) =>
        {
            if (ChargehandServer.OwnerOnly(ctx) is { } refused)
                return refused;
            if (await ReadAsync(ctx) is not { } parsed)
                return ChargehandServer.Json(ChargehandServer.Problem("the body must be {\"usd\": 15} with a number above 0, or {\"usd\": null} to switch the warning off"), StatusCodes.Status400BadRequest);
            try
            {
                limit.Set(parsed.Usd);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return ChargehandServer.Json(ChargehandServer.Problem($"the limit could not be written: {e.Message}"), StatusCodes.Status503ServiceUnavailable);
            }
            return ChargehandServer.Json(await BuildAsync(settings, log, limit, clock.GetUtcNow(), ctx.RequestAborted), StatusCodes.Status200OK);
        });

        app.Lifetime.ApplicationStarted.Register(() => _ = WatchAsync(settings, log, limit, clock, app.Lifetime.ApplicationStopping));
    }

    public static async Task<DailyWarningBody> BuildAsync(ServerSettings settings, IRunLog log, DailyWarningLimit limit, DateTimeOffset now, CancellationToken ct)
    {
        var spent = await SpentAsync(settings, log, now, ct);
        var cap = limit.LimitUsd;
        return new DailyWarningBody(Contract, cap, DailyWarningLimit.DefaultUsd, spent, cap is { } c && spent >= c, AccountPolicy.NextDay(now));
    }

    private static async Task<decimal> SpentAsync(ServerSettings settings, IRunLog log, DateTimeOffset now, CancellationToken ct)
    {
        decimal total = 0;
        foreach (var name in (settings.Accounts ?? []).Select(a => a.Name).Prepend(AccountSettings.Owner))
            total += (await log.SpendAsync(name, AccountPolicy.DayStart(now), ct)).Usd;
        return total;
    }

    /// <summary>Checks the day's spend each minute and writes one log line the first time it passes the limit.</summary>
    private static async Task WatchAsync(ServerSettings settings, IRunLog log, DailyWarningLimit limit, TimeProvider clock, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        try
        {
            do
            {
                try
                {
                    var now = clock.GetUtcNow();
                    var spent = await SpentAsync(settings, log, now, stop);
                    if (limit.FirstCrossing(now, spent))
                        Console.Error.WriteLine($"warning: the day's spend ${spent.ToString("0.00", CultureInfo.InvariantCulture)} passed the daily warning of ${limit.LimitUsd!.Value.ToString("0.00", CultureInfo.InvariantCulture)}; runs are not refused");
                }
                catch (Exception e) when (e is IOException or NotSupportedException or JsonException)
                {
                    // The spend could not be read this minute; the next tick tries again.
                }
            }
            while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException)
        {
            // The server is stopping; the watcher ends with it.
        }
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
