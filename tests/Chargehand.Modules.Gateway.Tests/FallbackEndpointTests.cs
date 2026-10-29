using Chargehand.Modules.Gateway.Api;

namespace Chargehand.Modules.Gateway.Tests;

/// <summary>ADR 0088: the fallback's base URL as the deployment gives it, a docker-network name with a port and a path prefix, without opening plain http to the internet.</summary>
public class FallbackEndpointTests
{
    [Theory]
    [InlineData(null, "https://api.anthropic.com", "")]
    [InlineData("", "https://api.anthropic.com", "")]
    [InlineData("https://gateway.example.test", "https://gateway.example.test", "")]
    [InlineData("https://Gateway.Example.Test/", "https://gateway.example.test", "")]
    [InlineData("https://gateway.example.test:8443/anthropic/", "https://gateway.example.test:8443", "/anthropic")]
    [InlineData("http://bifrost:8080/anthropic", "http://bifrost:8080", "/anthropic")]
    [InlineData("http://bifrost", "http://bifrost", "")]
    [InlineData("http://10.1.2.3:4001", "http://10.1.2.3:4001", "")]
    [InlineData("http://192.168.0.7/a/b", "http://192.168.0.7", "/a/b")]
    [InlineData("http://172.20.0.2", "http://172.20.0.2", "")]
    [InlineData("http://127.0.0.1:9", "http://127.0.0.1:9", "")]
    [InlineData("http://localhost:9", "http://localhost:9", "")]
    public void An_acceptable_base_url_keeps_its_port_and_path_prefix(string? url, string origin, string prefix)
    {
        var endpoint = FallbackEndpoint.Parse(url, out var why);

        Assert.NotNull(endpoint);
        Assert.Null(why);
        Assert.Equal((origin, prefix), (endpoint.Base.GetLeftPart(UriPartial.Authority), endpoint.PathPrefix));
        Assert.Equal(origin + prefix + "/v1/messages?beta=true", endpoint.For("/v1/messages?beta=true").ToString());
    }

    [Theory]
    [InlineData("http://gateway.example.test")]            // plain http to a public name
    [InlineData("http://8.8.8.8")]                          // or a public address
    [InlineData("http://172.32.0.1")]                       // just outside 172.16/12
    [InlineData("https://10.0.0.1")]                        // an address over https is still refused
    [InlineData("https://localhost")]
    [InlineData("ftp://bifrost")]
    [InlineData("http://user:pw@bifrost")]
    [InlineData("http://bifrost/a?b=1")]
    [InlineData("http://bifrost/a/../b")]
    [InlineData("http://bifrost/a%20b")]
    [InlineData("not a url")]
    public void Anything_else_is_refused_with_a_reason(string url)
    {
        Assert.Null(FallbackEndpoint.Parse(url, out var why));
        Assert.False(string.IsNullOrEmpty(why));
    }
}
