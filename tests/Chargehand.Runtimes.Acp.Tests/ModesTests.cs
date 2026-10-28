using AgentClientProtocol;
using Chargehand.Runtimes.Acp.Api;

namespace Chargehand.Runtimes.Acp.Tests;

public class ModesTests
{
    private static SessionModeState Modes(params string[] ids) => new(ids[0], [.. ids.Select(i => new SessionMode(i, i))]);

    [Fact]
    public void The_pinned_mode_wins_when_the_agent_lists_it()
    {
        Assert.Equal("read-only", AcpModes.ReadOnly(Modes("auto", "read-only", "plan"), "read-only"));
    }

    [Theory]
    [InlineData("default,plan,yolo", "plan")]
    [InlineData("auto,READ-ONLY,full-access", "READ-ONLY")]
    [InlineData("default,readonly", "readonly")]
    public void Without_a_pin_the_known_names_are_tried_in_order(string available, string expected)
    {
        Assert.Equal(expected, AcpModes.ReadOnly(Modes(available.Split(',')), pinned: null));
    }

    [Fact]
    public void A_pin_the_agent_does_not_list_falls_back_to_a_known_name()
    {
        Assert.Equal("plan", AcpModes.ReadOnly(Modes("default", "plan"), "read-only"));
    }

    [Fact]
    public void An_agent_with_no_such_mode_gets_none()
    {
        Assert.Null(AcpModes.ReadOnly(Modes("default", "yolo"), "read-only"));
        Assert.Null(AcpModes.ReadOnly(null, "read-only"));
    }
}
