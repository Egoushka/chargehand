using Chargehand.Egress;

namespace Chargehand.Tests;

public class EgressCliTests
{
    [Theory]
    [InlineData("--listen", "0.0.0.0:3128", "--allow", "api.anthropic.com,*.nuget.org")]
    public void Arguments_parse_into_a_listen_endpoint_and_an_allowlist(string a, string b, string c, string d)
    {
        var options = EgressCli.Parse([a, b, c, d], TextWriter.Null);
        Assert.Equal(3128, options!.Listen.Port);
        Assert.Equal("0.0.0.0", options.Listen.Address.ToString());
        Assert.Equal(["api.anthropic.com", "*.nuget.org"], options.Allow);
    }

    [Theory]
    [InlineData]
    [InlineData("--listen", "0.0.0.0:3128")]                                  // no allowlist
    [InlineData("--allow", "api.anthropic.com")]                              // no listen
    [InlineData("--listen", "nonsense", "--allow", "api.anthropic.com")]
    [InlineData("--listen", "0.0.0.0:3128", "--allow", "*")]                  // not a host pattern
    [InlineData("--listen", "0.0.0.0:3128", "--allow", "api.anthropic.com", "--extra")]
    public void A_bad_command_line_prints_usage_and_returns_no_options(params string[] args)
    {
        var error = new StringWriter();
        Assert.Null(EgressCli.Parse(args, error));
        Assert.Contains("usage: chargehand egress", error.ToString());
    }
}
