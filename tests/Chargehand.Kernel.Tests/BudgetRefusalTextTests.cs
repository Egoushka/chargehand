using Chargehand.Contracts;

namespace Chargehand.Kernel.Tests;

/// <summary>A spent budget is told from a rate limit by the text a gateway or provider relays (ADR 0095): the router takes a pool out until its refill for the first and until the next day for the second.</summary>
public class BudgetRefusalTextTests
{
    [Theory]
    [InlineData("402 Payment Required")]
    [InlineData("HTTP 402 from the gateway")]
    [InlineData("status: 402")]
    [InlineData("error 402 budget")]
    [InlineData("Budget has been exceeded for this key")]
    public void A_spent_budget_is_recognised_by_its_text(string message) => Assert.True(ChargehandException.BudgetRefused(message));

    [Theory]
    [InlineData("Rate limit exceeded")]
    [InlineData("429 Too Many Requests")]
    [InlineData("build 4020 failed")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_text_is_not_a_spent_budget(string? message) => Assert.False(ChargehandException.BudgetRefused(message));

    [Fact]
    public void A_spent_budget_ends_a_run_as_a_retryable_rate_limit()
    {
        var spent = ChargehandException.ErrorOf(new InvalidOperationException("402 Payment Required"));
        var limited = ChargehandException.ErrorOf(new InvalidOperationException("Rate limit exceeded"));
        var other = ChargehandException.ErrorOf(new InvalidOperationException("boom"));

        Assert.Equal((ErrorCode.RateLimited, true), (spent.Code, spent.Retryable));
        Assert.Equal((ErrorCode.RateLimited, true), (limited.Code, limited.Retryable));
        Assert.Equal(ErrorCode.Internal, other.Code);
    }
}
