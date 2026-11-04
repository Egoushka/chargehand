using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>ADR 0091: the server-wide soft daily dollar limit, edited from the console, that warns and never refuses.</summary>
public class DailyWarningTests
{
    [Fact]
    public void The_limit_defaults_to_fifteen_and_a_new_process_reads_the_one_set()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "limits", "daily-warning.json");
        Assert.Equal(15m, new DailyWarningLimit(path).LimitUsd);

        new DailyWarningLimit(path).Set(22.5m);
        Assert.Equal(22.5m, new DailyWarningLimit(path).LimitUsd);

        new DailyWarningLimit(path).Set(null);
        Assert.Null(new DailyWarningLimit(path).LimitUsd);
    }

    [Fact]
    public void The_warning_is_raised_once_per_UTC_day_and_only_at_or_past_the_limit()
    {
        var limit = new DailyWarningLimit(null);
        var noon = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        Assert.False(limit.FirstCrossing(noon, 14.99m));
        Assert.True(limit.FirstCrossing(noon, 15m));
        Assert.False(limit.FirstCrossing(noon.AddHours(1), 20m));
        Assert.True(limit.FirstCrossing(noon.AddDays(1), 16m));

        limit.Set(null);
        Assert.False(limit.FirstCrossing(noon.AddDays(2), 1000m));
    }

    [Fact]
    public async Task The_console_reads_and_sets_the_limit_and_refuses_one_that_is_not_above_zero()
    {
        await using var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));

        var read = await server.Http.GetFromJsonAsync<JsonElement>("/v1/limits/daily-warning");
        Assert.Equal("daily-warning/v1", read.GetProperty("contract_version").GetString());
        Assert.Equal((15m, 15m, false), (read.GetProperty("limit_usd").GetDecimal(), read.GetProperty("default_usd").GetDecimal(), read.GetProperty("exceeded").GetBoolean()));

        var put = await server.Http.PutAsJsonAsync("/v1/limits/daily-warning", new { usd = 20 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(20m, (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("limit_usd").GetDecimal());

        var off = await server.Http.PutAsJsonAsync("/v1/limits/daily-warning", new { usd = (decimal?)null });
        Assert.False((await off.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("limit_usd", out var limit) && limit.ValueKind != JsonValueKind.Null);

        foreach (var bad in new object[] { new { usd = 0 }, new { usd = -1 }, new { usd = "15" }, new { other = 1 } })
            Assert.Equal(HttpStatusCode.BadRequest, (await server.Http.PutAsJsonAsync("/v1/limits/daily-warning", bad)).StatusCode);
    }
}
