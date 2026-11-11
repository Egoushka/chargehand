using System.Text.Json;
using Chargehand.Modules.Gateway.Api;

namespace Chargehand.Modules.Gateway.Tests;

/// <summary>ADR 0060: <c>driven.network</c> binds without reflection, with the profile's snake_case keys.</summary>
public sealed class GatewaySettingsJsonContextTests
{
    [Fact]
    public void Driven_network_binds_from_the_profile_shape()
    {
        var network = JsonSerializer.Deserialize(
            """{ "allow": ["registry.example"], "outside": "server-net", "mcp_forward": "server.internal:4300", "model_url": "http://gateway.internal:8080/v1", "otlp_url": "http://collector.internal:4318", "unknown": 1 }""",
            GatewaySettingsJsonContext.Default.DrivenNetwork)!;

        Assert.Equal(["registry.example"], network.Allow);
        Assert.Equal("server-net", network.Outside);
        Assert.Equal("server.internal:4300", network.McpForward);
        Assert.Equal("http://gateway.internal:8080/v1", network.ModelUrl);
        Assert.Equal("http://collector.internal:4318", network.OtlpUrl);
    }

    [Fact]
    public void An_empty_block_is_every_key_at_its_default() =>
        Assert.Equal(new DrivenNetwork(), JsonSerializer.Deserialize("{}", GatewaySettingsJsonContext.Default.DrivenNetwork));
}
