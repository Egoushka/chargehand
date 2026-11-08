using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Modules.Sessions.Tests;

/// <summary>ADR 0096: when a run that met the subscription's limit waits, and for how long.</summary>
public class QuotaWaitTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Plenty = TimeSpan.FromHours(6);

    [Fact]
    public void A_limit_with_a_reset_waits_to_the_reset_plus_the_margin()
    {
        var epoch = Now.AddMinutes(90).ToUnixTimeSeconds();
        var delay = QuotaWait.Delay(true, $"Claude AI usage limit reached|{epoch}", 429, Now, 0, Plenty);
        Assert.Equal(TimeSpan.FromMinutes(90) + QuotaWait.Margin, delay);
        Assert.Equal(TimeSpan.FromSeconds(30) + QuotaWait.Margin, QuotaWait.Delay(true, "API Error: 429 rate_limit_error, retry-after: 30", 429, Now, 0, Plenty));
    }

    [Theory]
    [InlineData("API Error: 429 rate limit exceeded")]
    [InlineData("Claude AI usage limit reached|1000000000")]
    public void A_limit_with_no_reset_or_a_past_one_waits_the_default_backoff(string text) =>
        Assert.Equal(UsageLimit.DefaultBackoff + QuotaWait.Margin, QuotaWait.Delay(true, text, 429, Now, 0, Plenty));

    [Theory]
    [InlineData(true, "API Error: 402 payment required", null)]
    [InlineData(true, "Max budget exceeded status 429", 429)]
    [InlineData(true, "API Error: 500 internal error", 500)]
    [InlineData(false, "API Error: 429 rate limit exceeded", 429)]
    public void A_budget_refusal_or_any_other_error_is_never_waited_out(bool isError, string text, int? status) =>
        Assert.Null(QuotaWait.Delay(isError, text, status, Now, 0, Plenty));

    [Fact]
    public void A_reset_past_the_room_or_the_longest_wait_and_a_fifth_limit_are_not_waited_for()
    {
        var farther = $"usage limit reached|{Now.AddHours(7).ToUnixTimeSeconds()}";
        Assert.Null(QuotaWait.Delay(true, farther, 429, Now, 0, TimeSpan.FromDays(2)));
        var near = $"usage limit reached|{Now.AddMinutes(20).ToUnixTimeSeconds()}";
        Assert.Null(QuotaWait.Delay(true, near, 429, Now, 0, TimeSpan.FromMinutes(10)));
        Assert.NotNull(QuotaWait.Delay(true, near, 429, Now, QuotaWait.MostWaits - 1, Plenty));
        Assert.Null(QuotaWait.Delay(true, near, 429, Now, QuotaWait.MostWaits, Plenty));
    }
}
