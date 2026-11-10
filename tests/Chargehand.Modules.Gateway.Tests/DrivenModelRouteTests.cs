using Chargehand.Kernel;
using Chargehand.Modules.Gateway.Api;

namespace Chargehand.Modules.Gateway.Tests;

/// <summary>ADR 0039: the model gateway URL becomes an egress forward (http) or an allowlisted host (https).</summary>
public class DrivenModelRouteTests
{
    private static string Url(int port) => $"http://callback:{port}";

    [Fact]
    public void Http_gateway_is_reached_through_a_forward()
    {
        var route = DrivenModelRoute.Parse("http://gw.example.test:8080/anthropic/", true, 9000, Url)!;
        Assert.Equal("8080=gw.example.test:8080", route.Forward);
        Assert.Equal(8080, route.ForwardPort);
        Assert.Equal("http://callback:8080/anthropic", route.BaseUrl);
        Assert.Null(route.AllowHost);
    }

    [Fact]
    public void Https_gateway_is_allowlisted()
    {
        var route = DrivenModelRoute.Parse("https://gw.example.test/v1/", true, null, Url)!;
        Assert.Null(route.Forward);
        Assert.Equal("gw.example.test", route.AllowHost);
        Assert.Equal("https://gw.example.test/v1", route.BaseUrl);
    }

    [Fact]
    public void Unset_url_is_no_route() => Assert.Null(DrivenModelRoute.Parse(null, false, null, Url));

    [Theory]
    [InlineData("http://gw.example.test:8080", false, null)]
    [InlineData("ftp://gw.example.test", true, null)]
    [InlineData("https://user:pw@gw.example.test", true, null)]
    [InlineData("https://gw.example.test?x=1", true, null)]
    [InlineData("http://gw.example.test:9000", true, 9000)]
    public void Unusable_urls_are_refused(string url, bool priced, int? mcpPort) =>
        Assert.Throws<ChargehandException>(() => DrivenModelRoute.Parse(url, priced, mcpPort, Url));
}
