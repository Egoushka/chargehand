using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Research.Api;

namespace Chargehand.Modules.Research.Tests;

/// <summary>ADR 0040, ADR 0060: <c>prompt_enhancer</c> binds without reflection, names a server of <c>mcp_servers</c>, and keeps its deadline in range.</summary>
public sealed class PromptEnhancerSettingsTests
{
    [Fact]
    public void Prompt_enhancer_binds_from_the_profile_shape_and_defaults_its_deadline()
    {
        var settings = JsonSerializer.Deserialize("""{ "server": "whetstone", "deadline_ms": 300 }""", ResearchSettingsJsonContext.Default.PromptEnhancerSettings)!;

        Assert.Equal(new PromptEnhancerSettings("whetstone", 300), settings);
        Assert.Equal(300, settings.EffectiveDeadlineMs);
        Assert.Equal(1500, new PromptEnhancerSettings("whetstone").EffectiveDeadlineMs);
    }

    [Fact]
    public void A_server_that_mcp_servers_does_not_name_is_refused()
    {
        var e = Assert.Throws<ChargehandException>(() => new PromptEnhancerSettings("nope").Validate(["whetstone"]));

        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Equal("prompt_enhancer.server 'nope' is not defined in mcp_servers", e.Message);
    }

    [Theory]
    [InlineData(49)]
    [InlineData(30_001)]
    public void A_deadline_out_of_range_is_refused_and_one_in_range_passes(int deadlineMs)
    {
        new PromptEnhancerSettings("whetstone", PromptEnhancerSettings.MinDeadlineMs).Validate(["whetstone"]);
        new PromptEnhancerSettings("whetstone", PromptEnhancerSettings.MaxDeadlineMs).Validate(["whetstone"]);

        var e = Assert.Throws<ChargehandException>(() => new PromptEnhancerSettings("whetstone", deadlineMs).Validate(["whetstone"]));

        Assert.Equal("prompt_enhancer.deadline_ms must be 50 to 30000", e.Message);
    }
}
