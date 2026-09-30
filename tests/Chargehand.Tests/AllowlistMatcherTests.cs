using System.Net;
using Chargehand.Egress;

namespace Chargehand.Tests;

/// <summary>Driven sessions (ADR 0039): which hosts a session container may reach, and which addresses no allowlist entry can unlock.</summary>
public class AllowlistMatcherTests
{
    private static readonly AllowlistMatcher Matcher = new(["api.anthropic.com", "*.nuget.org", ".pythonhosted.org", "Registry.NPMJS.org."]);

    [Theory]
    [InlineData("api.anthropic.com", 443, true)]
    [InlineData("API.Anthropic.COM", 443, true)]                 // case
    [InlineData("api.anthropic.com.", 443, true)]                // trailing dot
    [InlineData("registry.npmjs.org", 443, true)]                // entry written with capitals and a dot
    [InlineData("api.nuget.org", 443, true)]                     // wildcard entry, subdomain
    [InlineData("nuget.org", 443, false)]                        // wildcard does not cover the apex
    [InlineData("files.pythonhosted.org", 443, true)]            // leading-dot entry, subdomain
    [InlineData("evilnuget.org", 443, false)]                    // suffix must be on a label boundary
    [InlineData("api.nuget.org.evil.test", 443, false)]
    [InlineData("api.anthropic.com", 80, false)]                 // only 443
    [InlineData("api.anthropic.com", 8443, false)]
    [InlineData("other.example", 443, false)]
    [InlineData("", 443, false)]
    public void Host_and_port_are_matched_exactly(string host, int port, bool allowed) =>
        Assert.Equal(allowed, Matcher.Check(host, port).Allowed);

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public void An_ip_literal_or_localhost_is_refused_even_when_listed(string host) =>
        Assert.False(new AllowlistMatcher([host, "api.anthropic.com"]).Check(host, 443).Allowed);

    [Theory]
    [InlineData("[::1]")]
    [InlineData("::1")]
    [InlineData("münchen.example")]                              // non-ASCII: refused, not punycode-guessed
    [InlineData("api.anthropic.com\r\nHost: x")]
    public void An_odd_name_is_refused_as_an_entry_and_as_a_request(string host)
    {
        Assert.Throws<ArgumentException>(() => new AllowlistMatcher([host]));
        Assert.False(Matcher.Check(host, 443).Allowed);
    }

    [Fact]
    public void A_refusal_says_why()
    {
        Assert.Contains("port", Matcher.Check("api.anthropic.com", 80).Reason);
        Assert.Contains("not on the allowlist", Matcher.Check("other.example", 443).Reason);
        Assert.Contains("IP literal", Matcher.Check("1.1.1.1", 443).Reason);
    }

    [Fact]
    public void An_entry_that_is_not_a_host_pattern_is_refused_at_construction()
    {
        Assert.Throws<ArgumentException>(() => new AllowlistMatcher(["*"]));
        Assert.Throws<ArgumentException>(() => new AllowlistMatcher(["a b"]));
        Assert.Throws<ArgumentException>(() => new AllowlistMatcher(["https://api.anthropic.com"]));
        Assert.Throws<ArgumentException>(() => new AllowlistMatcher(["api.anthropic.com:443"]));
        Assert.Throws<ArgumentException>(() => new AllowlistMatcher(["*.*"]));
        Assert.Throws<ArgumentException>(() => new AllowlistMatcher([]));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("160.79.104.10", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]                       // cloud metadata
    [InlineData("100.100.1.1", false)]                            // carrier-grade NAT, also used by overlay networks
    [InlineData("100.127.255.255", false)]
    [InlineData("100.128.0.1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]                       // IPv4-mapped private
    [InlineData("::ffff:8.8.8.8", true)]
    public void Only_public_addresses_pass(string address, bool expected) =>
        Assert.Equal(expected, AllowlistMatcher.IsPublic(IPAddress.Parse(address)));
}
