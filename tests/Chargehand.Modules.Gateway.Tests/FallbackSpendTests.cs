using Chargehand.Modules.Gateway.Api;

namespace Chargehand.Modules.Gateway.Tests;

/// <summary>ADR 0088, decision 6: at teardown the server reads what the fallback cost from the egress container's log. The tail it reads can start in the middle of a long batch, so each fallback
/// line carries running totals, and the last one it holds is the batch's.</summary>
public class FallbackSpendTests
{
    private const string Connection = """{"at":"2026-10-29T09:00:00+00:00","run":"t1","host":"registry.example.test","port":443,"allowed":true,"reason":""}""";
    private const string Plain = """{"at":"2026-10-29T09:00:01+00:00","model_run":"t1","status":200,"tokens":10,"reason":""}""";

    private static string Call(string run, int status, decimal usd, decimal total, int calls, string? limitedUntil = "2026-10-29T14:00:00+00:00", bool closed = false) =>
        $$"""{"at":"2026-10-29T09:05:00+00:00","model_run":"{{run}}","status":{{status}},"tokens":5,"reason":"","fallback":true,"usd":{{usd.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"total_usd":{{total.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"calls":{{calls}}{{(closed ? ",\"closed\":true" : "")}}{{(limitedUntil is null ? "" : $",\"limited_until\":\"{limitedUntil}\"")}}}""";

    [Fact]
    public void A_log_without_fallback_calls_spent_nothing()
    {
        var spend = FallbackSpend.Parse(string.Join('\n', Connection, Plain, "model endpoint listening on 0.0.0.0:3129, forwarding to api.example.test"));
        Assert.Equal(FallbackSpend.None, spend);
    }

    [Fact]
    public void The_last_fallback_line_holds_the_batchs_totals_and_each_run_is_summed_from_its_own_lines()
    {
        var log = string.Join('\n', Connection, Call("t1", 200, 0.5m, 0.5m, 1), Plain, Call("t2", 200, 0.25m, 0.75m, 2), Connection, Call("t1", 200, 1m, 1.75m, 3, "2026-10-29T15:00:00+00:00"));

        var spend = FallbackSpend.Parse(log);

        Assert.Equal((1.75m, 3, false), (spend.Usd, spend.Calls, spend.Refused));
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T15:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture), spend.LimitedUntil);
        Assert.Equal((1.5m, 2), (spend.ByRun["t1"].Usd, spend.ByRun["t1"].Calls));
        Assert.Equal((0.25m, 1), (spend.ByRun["t2"].Usd, spend.ByRun["t2"].Calls));
    }

    [Fact]
    public void A_tail_that_lost_the_early_lines_still_gives_the_totals()
    {
        var spend = FallbackSpend.Parse(string.Join('\n', "ronment}\"}", Call("t1", 200, 1m, 7.25m, 40)));
        Assert.Equal((7.25m, 40), (spend.Usd, spend.Calls));
    }

    [Fact]
    public void A_402_from_the_fallback_closes_it()
    {
        var spend = FallbackSpend.Parse(string.Join('\n', Call("t1", 200, 1m, 1m, 1), Call("t1", 402, 0m, 1m, 2, closed: true)));
        Assert.True(spend.Refused);
        Assert.Equal((1m, 2), (spend.Usd, spend.Calls));
    }

    [Fact]
    public void Several_containers_add_up()
    {
        var a = FallbackSpend.Parse(Call("t1", 200, 1m, 1m, 1, "2026-10-29T14:00:00+00:00"));
        var b = FallbackSpend.Parse(Call("t1", 402, 0m, 2m, 3, "2026-10-29T16:00:00+00:00", closed: true));

        var both = a.Plus(b);

        Assert.Equal((3m, 4, true), (both.Usd, both.Calls, both.Refused));
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T16:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture), both.LimitedUntil);
        Assert.Equal((1m, 2), (both.ByRun["t1"].Usd, both.ByRun["t1"].Calls));
        Assert.Equal(a, a.Plus(FallbackSpend.None));
    }
}
