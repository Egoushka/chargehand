using Chargehand.Automation;

namespace Chargehand.Tests;

public class SweepLabelsTests
{
    [Fact]
    public void The_set_holds_the_agent_and_size_labels()
    {
        Assert.Equal(["agent:ready", "agent:needs-you", "agent:blocked", "agent:epic", "agent:skip", "size:S", "size:M", "size:L"], SweepLabels.All.Select(l => l.Name));
        Assert.All(SweepLabels.All, l => Assert.Matches("^[0-9a-f]{6}$", l.Color));
        Assert.All(SweepLabels.All, l => Assert.NotEmpty(l.Description));
    }
}
