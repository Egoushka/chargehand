using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Runtimes.ClaudeCode.Api;
using Xunit;

namespace Chargehand.Tests;

public sealed class ClaudeCodeCustomHeadersTests
{
    private static readonly ClaudeCodeSettings Gateway = new("2.1.283", OauthTokenSecret: "t", BaseUrl: "http://gateway.internal/anthropic", CustomHeadersSecret: "h");

    [Fact]
    public void Header_lines_are_trimmed_and_joined()
    {
        Assert.Equal("x-bf-vk: k1\nx-b: 2", Gateway.CustomHeaders(" x-bf-vk: k1 \r\n\nx-b: 2\n"));
        Assert.Equal(["k1", "2"], ClaudeCodeSettings.HeaderValues("x-bf-vk: k1\r\nx-b:2\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no colon")]
    [InlineData(": value")]
    [InlineData("name:")]
    [InlineData("bad name: v")]
    public void A_line_that_is_not_name_colon_value_is_refused(string value) =>
        Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ChargehandException>(() => Gateway.CustomHeaders(value)).Code);

    [Fact]
    public void Headers_without_a_gateway_are_refused() =>
        Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ChargehandException>(() => (Gateway with { BaseUrl = null }).CustomHeaders("x: 1")).Code);
}
