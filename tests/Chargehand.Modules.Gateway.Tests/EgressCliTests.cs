using Chargehand.Modules.Gateway.Api;

namespace Chargehand.Modules.Gateway.Tests;

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

    private static Func<string, string?> Env(params (string Name, string Value)[] pairs) => name => pairs.FirstOrDefault(p => p.Name == name).Value;

    [Fact]
    public void The_fallback_comes_from_the_environment_all_or_nothing()
    {
        var env = Env((EgressCli.FallbackCredentialVariable, "k"), (EgressCli.FallbackHostVariable, "fallback.example.test"), (EgressCli.FallbackUsdVariable, "2.50"), (EgressCli.FallbackPriceVariable, "3,15,0.3,3.75"));
        var fallback = EgressCli.FallbackFromEnvironment(env, TextWriter.Null, out var valid);
        Assert.True(valid);
        Assert.Equal(new GatewayFallback("k", "fallback.example.test", 2.5m, new FallbackPrice(3m, 15m, 0.3m, 3.75m)), fallback);

        Assert.Null(EgressCli.FallbackFromEnvironment(Env(), TextWriter.Null, out var none));
        Assert.True(none);
    }

    [Theory]
    [InlineData("k", "fallback.example.test", "0", "3,15,0.3,3.75")]       // no allowance
    [InlineData("k", "fallback.example.test", "2", "3,15,0.3")]             // three prices
    [InlineData("k", "fallback.example.test", "2", "3,15,x,3.75")]
    [InlineData("k", "10.0.0.1", "2", "3,15,0.3,3.75")]                      // an address
    [InlineData("", "fallback.example.test", "2", "3,15,0.3,3.75")]         // no key
    public void A_partly_set_or_wrong_fallback_is_refused_without_printing_a_value(string key, string host, string usd, string price)
    {
        var error = new StringWriter();
        var env = Env((EgressCli.FallbackCredentialVariable, key), (EgressCli.FallbackHostVariable, host), (EgressCli.FallbackUsdVariable, usd), (EgressCli.FallbackPriceVariable, price));

        Assert.Null(EgressCli.FallbackFromEnvironment(env, error, out var valid));

        Assert.False(valid);
        Assert.NotEmpty(error.ToString());
    }
}
