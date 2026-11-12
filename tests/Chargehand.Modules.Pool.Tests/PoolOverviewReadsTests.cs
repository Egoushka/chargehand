using Chargehand.Modules.Pool.Api;

namespace Chargehand.Modules.Pool.Tests;

public sealed class PoolOverviewReadsTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Done_since_keeps_the_newest_within_the_window_oldest_first()
    {
        using var kit = new PoolKit();
        var cutoff = kit.Time.GetUtcNow();
        for (var i = 0; i < 5; i++)
        {
            var item = await kit.AddAsync("a", $"d{i}");
            await kit.Pool.DoneAsync([item.Key], null, None);
        }

        var capped = await kit.Pool.DoneSinceAsync(cutoff, 3, None);
        var after = await kit.Pool.DoneSinceAsync(kit.Time.GetUtcNow().AddSeconds(1), 3, None);

        Assert.Equal(["d2", "d3", "d4"], capped.Select(d => d.Title));
        Assert.Empty(after);
    }

    [Fact]
    public async Task Queued_is_capped_in_enqueue_order_and_leaves_claimed_and_done_out()
    {
        using var kit = new PoolKit();
        var done = await kit.AddAsync("a", "finished");
        await kit.Pool.DoneAsync([done.Key], null, None);
        for (var i = 0; i < 4; i++)
            await kit.AddAsync("b", $"q{i}");

        var rows = await kit.Pool.QueuedAsync(3, None);

        Assert.Equal(["q0", "q1", "q2"], rows.Select(r => r.Title));
    }

    [Fact]
    public async Task Live_workers_count_their_claimed_items_and_leave_released_ones_out()
    {
        using var kit = new PoolKit();
        await kit.AddAsync("a", "a1");
        await kit.AddAsync("a", "a2");
        await kit.AddAsync("b", "b1");
        var first = (await kit.Pool.TickAsync(None)).Worker!;
        var second = (await kit.Pool.TickAsync(None)).Worker!;
        Assert.True(await kit.Pool.ReleaseAsync(second, new ResumeCard("d", "l", "s"), None));

        var live = await kit.Pool.LiveWorkersAsync(10, None);

        var worker = Assert.Single(live);
        Assert.Equal((first, 2), (worker.Id, worker.Items));
    }
}
