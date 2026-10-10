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

    [Fact]
    public void Forwards_parse_beside_the_allowlist()
    {
        var options = EgressCli.Parse(["--listen", "0.0.0.0:3128", "--allow", "api.anthropic.com", "--forward", "4301=chargehand:4300", "--forward", "4302=other:81"], TextWriter.Null);
        Assert.Equal([new PortForward(4301, "chargehand", 4300), new PortForward(4302, "other", 81)], options!.Forwards);
    }

    [Theory]
    [InlineData("--forward", "nonsense")]
    [InlineData("--forward", "3128=host:80")]
    public void A_bad_forward_is_a_usage_error(string flag, string value) =>
        Assert.Null(EgressCli.Parse(["--listen", "0.0.0.0:3128", "--allow", "api.anthropic.com", flag, value], TextWriter.Null));

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

    [Fact]
    public void The_model_endpoint_has_a_default_host_and_takes_another()
    {
        var plain = EgressCli.Parse(["--listen", "0.0.0.0:3128", "--allow", "api.anthropic.com"], TextWriter.Null)!;
        Assert.Null(plain.ModelListen);
        var model = EgressCli.Parse(["--listen", "0.0.0.0:3128", "--allow", "api.anthropic.com", "--model-listen", "0.0.0.0:3129"], TextWriter.Null)!;
        Assert.Equal(3129, model.ModelListen!.Port);
        Assert.Equal("api.anthropic.com", model.ModelHost);
        Assert.Equal("gateway.example.com", EgressCli.Parse(["--listen", "0.0.0.0:3128", "--allow", "x.com", "--model-listen", "0.0.0.0:3129", "--model-host", "gateway.example.com"], TextWriter.Null)!.ModelHost);
    }

    [Theory]
    [InlineData("--model-host", "10.0.0.1")]
    [InlineData("--model-host", "localhost")]
    [InlineData("--model-listen", "nonsense")]
    public void A_bad_model_option_is_a_usage_error(string flag, string value) =>
        Assert.Null(EgressCli.Parse(["--listen", "0.0.0.0:3128", "--allow", "api.anthropic.com", flag, value], TextWriter.Null));

    [Fact]
    public async Task A_model_endpoint_without_its_secrets_in_the_environment_does_not_start()
    {
        Environment.SetEnvironmentVariable(EgressCli.CredentialVariable, null);
        Environment.SetEnvironmentVariable(EgressCli.KeyVariable, null);
        var error = new StringWriter();
        Assert.Equal(2, await EgressCli.RunAsync(["--listen", "127.0.0.1:0", "--allow", "api.anthropic.com", "--model-listen", "127.0.0.1:0"], error, default));
        Assert.Contains(EgressCli.CredentialVariable, error.ToString());
    }
}
