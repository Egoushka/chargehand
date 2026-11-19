using Chargehand.Modules.Pool.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

public sealed class PoolRunStarterTests
{
    private static PoolItem Item(string title, int? issue = null, string? size = null) =>
        new("k-" + title, "enqueue", "owner/app", "docs", title, "body", 0, null, DateTimeOffset.UnixEpoch, PoolStatus.Queued, null, null, null, null, issue, size);

    [Theory]
    [InlineData("S", 3)]
    [InlineData("M", 6)]
    [InlineData(null, 6)]
    [InlineData("L", 12)]
    public void Hand_offs_by_size(string? size, int expected) => Assert.Equal(expected, PoolRunStarter.MaxHandOffsFor(size));

    [Fact]
    public void Goal_says_fixes_the_issue()
    {
        var request = new PoolStartRequest(new PoolScope("owner/app", "docs"), "w1", [Item("One", issue: 12), Item("Two")], null);
        Assert.EndsWith("Fixes #12.\n", PoolRunStarter.Goal(request));
        Assert.DoesNotContain("Fixes", PoolRunStarter.Goal(request with { Items = [Item("Two")] }));
    }

    [Fact]
    public void The_largest_size_of_a_batch_wins()
    {
        Assert.Equal("L", PoolRunStarter.LargestSize([Item("a", size: "S"), Item("b", size: "L"), Item("c")]));
        Assert.Equal("S", PoolRunStarter.LargestSize([Item("a", size: "S")]));
        Assert.Null(PoolRunStarter.LargestSize([Item("a")]));
    }

    [Fact]
    public async Task A_lost_run_counts_as_a_breaker_failure()
    {
        var breaker = new PoolBreakerCheck(null, TimeProvider.System);
        var scope = new PoolScope("owner/app", "docs");
        Assert.True(PoolRunStarter.CountsAsFailure(null));
        for (var i = 0; i < PoolBreakerCheck.Failures; i++)
            breaker.Finished(scope.Repository, PoolRunStarter.CountsAsFailure(null));
        Assert.NotNull(await breaker.HoldAsync(scope, CancellationToken.None));
    }
}
