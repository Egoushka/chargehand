using AgentClientProtocol;
using Chargehand.Kernel;
using Chargehand.Runtimes.Acp.Api;

namespace Chargehand.Runtimes.Acp.Tests;

public class UsageMeterTests
{
    [Fact]
    public void Before_the_agent_reports_anything_nothing_is_recorded()
    {
        Assert.Null(new AcpUsageMeter().Tokens);
    }

    [Fact]
    public void While_a_turn_runs_the_context_gauge_stands_in_for_input_tokens()
    {
        var meter = new AcpUsageMeter();
        meter.Observe(new UsageUpdate(Used: 1200, Size: 100000));
        meter.Observe(new UsageUpdate(Used: 3400, Size: 100000));

        Assert.Equal(new TokenCounts(3400, 0, 0, 0, 0), meter.Tokens);
    }

    [Fact]
    public void The_turn_result_replaces_the_gauge()
    {
        var meter = new AcpUsageMeter();
        meter.Observe(new UsageUpdate(Used: 3400, Size: 100000));
        meter.Finish(new TokenUsage(InputTokens: 900, OutputTokens: 50, ThoughtTokens: 20, CachedReadTokens: 300, CachedWriteTokens: 10));

        Assert.Equal(new TokenCounts(900, 50, 20, 300, 10), meter.Tokens);
    }

    [Fact]
    public void A_result_without_usage_keeps_the_gauge()
    {
        var meter = new AcpUsageMeter();
        meter.Observe(new UsageUpdate(Used: 3400, Size: 100000));
        meter.Finish(null);

        Assert.Equal(new TokenCounts(3400, 0, 0, 0, 0), meter.Tokens);
    }

    [Fact]
    public void A_total_with_no_split_counts_as_input_so_a_cap_still_sees_it()
    {
        var meter = new AcpUsageMeter();
        meter.Finish(new TokenUsage(TotalTokens: 777));

        Assert.Equal(new TokenCounts(777, 0, 0, 0, 0), meter.Tokens);
    }

    [Fact]
    public void A_new_turn_starts_from_nothing()
    {
        var meter = new AcpUsageMeter();
        meter.Finish(new TokenUsage(InputTokens: 10, OutputTokens: 1));
        meter.Reset();

        Assert.Null(meter.Tokens);
    }
}
