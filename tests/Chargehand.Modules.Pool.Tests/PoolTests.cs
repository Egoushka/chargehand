using Chargehand.Modules.Pool.Api;

namespace Chargehand.Modules.Pool.Tests;

public sealed class PoolTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Enqueue_writes_a_queued_item_and_never_starts()
    {
        using var kit = new PoolKit();

        var item = await kit.AddAsync("docs", "one");

        var stored = Assert.Single(await kit.Pool.ItemsAsync(null, None));
        Assert.Equal(item.Key, stored.Key);
        Assert.Equal(PoolStatus.Queued, stored.Status);
        Assert.Null(stored.Worker);
        Assert.Empty(await kit.Pool.WorkersAsync(None));
        Assert.Empty(kit.Starter.Requests);
    }

    [Fact]
    public async Task Scope_claims_every_queued_item_oldest_first()
    {
        using var kit = new PoolKit();
        var a = new List<string>();
        var b = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            a.Add((await kit.AddAsync("a", "a" + i)).Key);
            if (i < 2)
                b.Add((await kit.AddAsync("b", "b" + i)).Key);
        }

        var first = await kit.Pool.TickAsync(None);
        var second = await kit.Pool.TickAsync(None);

        Assert.NotNull(first.Worker);
        Assert.NotNull(second.Worker);
        Assert.Equal(2, kit.Starter.Requests.Count);
        Assert.Equal(a, kit.Starter.Requests[0].Items.Select(i => i.Key));
        Assert.Equal(new PoolScope("owner/repo", "a"), kit.Starter.Requests[0].Scope);
        Assert.Equal(b, kit.Starter.Requests[1].Items.Select(i => i.Key));
        Assert.Empty((await kit.Pool.NextAsync(first.Worker!, None)).Items);
        Assert.Empty((await kit.Pool.NextAsync(second.Worker!, None)).Items);
        Assert.All(await kit.Pool.ItemsAsync(null, None), i => Assert.Equal(PoolStatus.Claimed, i.Status));
        Assert.Equal("empty", (await kit.Pool.TickAsync(None)).Reason);
    }

    [Fact]
    public async Task Item_enqueued_into_a_live_scope_goes_to_its_worker()
    {
        using var kit = new PoolKit();
        await kit.AddAsync("a", "first");
        var worker = (await kit.Pool.TickAsync(None)).Worker!;

        var late = await kit.AddAsync("a", "late");

        Assert.Equal("empty", (await kit.Pool.TickAsync(None)).Reason);
        var next = await kit.Pool.NextAsync(worker, None);
        Assert.Null(next.Reason);
        Assert.Equal(late.Key, Assert.Single(next.Items).Key);
        Assert.Single(kit.Starter.Requests);
        Assert.Single(await kit.Pool.WorkersAsync(None));
    }

    [Fact]
    public async Task Governor_orders_pull_requests_then_priority_then_age()
    {
        using var kit = new PoolKit();
        await kit.AddAsync("old", "old");
        await kit.AddAsync("new", "new", priority: 2);
        await kit.AddAsync("newer", "newer", pullRequest: 7);

        for (var i = 0; i < 3; i++)
            await kit.Pool.TickAsync(None);

        Assert.Equal(["newer", "new", "old"], kit.Starter.Requests.Select(r => r.Scope.Area));
    }

    [Fact]
    public async Task Failed_check_leaves_the_scope_queued_with_its_name()
    {
        var box = new FakeCheck("box") { Detail = "memory is short" };
        using var kit = new PoolKit(new FakeCheck("money"), box);
        await kit.AddAsync("a", "one");
        await kit.AddAsync("a", "two");

        var tick = await kit.Pool.TickAsync(None);

        Assert.Null(tick.Worker);
        Assert.Equal("box", tick.Reason);
        Assert.Equal("memory is short", tick.Detail);
        Assert.Empty(kit.Starter.Requests);
        var items = await kit.Pool.ItemsAsync(null, None);
        Assert.Equal(2, items.Count);
        Assert.All(items, i =>
        {
            Assert.Equal(PoolStatus.Queued, i.Status);
            Assert.Equal("box", i.Reason);
        });
    }

    [Fact]
    public async Task Decisions_valve_holds_every_source_and_frees_one_when_one_closes()
    {
        var open = 6;
        using var kit = new PoolKit(new DecisionsCheck(_ => Task.FromResult(open), 5));
        await kit.AddAsync("a", "one");
        await kit.AddAsync("b", "two", pullRequest: 3);

        var held = await kit.Pool.TickAsync(None);
        var heldAgain = await kit.Pool.TickAsync(None);

        Assert.Equal("decisions", held.Reason);
        Assert.Equal("decisions", heldAgain.Reason);
        Assert.Empty(kit.Starter.Requests);
        Assert.All(await kit.Pool.ItemsAsync(new PoolScope("owner/repo", "b"), None), i => Assert.Equal("decisions", i.Reason));

        open = 5;
        var started = await kit.Pool.TickAsync(None);

        Assert.NotNull(started.Worker);
        Assert.Single(kit.Starter.Requests);
    }

    [Fact]
    public async Task Starter_refusal_is_a_reason()
    {
        using var kit = new PoolKit();
        await kit.AddAsync("a", "one");
        kit.Starter.Refusal = "the account is over its cap";

        var refused = await kit.Pool.TickAsync(None);

        Assert.Equal("starter", refused.Reason);
        Assert.Equal("the account is over its cap", refused.Detail);
        var item = Assert.Single(await kit.Pool.ItemsAsync(null, None));
        Assert.Equal(PoolStatus.Queued, item.Status);
        Assert.Equal("starter", item.Reason);

        kit.Starter.Refusal = null;
        var started = await kit.Pool.TickAsync(None);

        Assert.NotNull(started.Worker);
        Assert.Equal(2, kit.Starter.Requests.Count);
        Assert.Equal(item.Key, Assert.Single(kit.Starter.Requests[1].Items).Key);
    }

    [Fact]
    public async Task A_scope_the_starter_refuses_does_not_stall_the_scopes_behind_it()
    {
        using var kit = new PoolKit();
        await kit.AddAsync("a", "first in order", pullRequest: 4);
        var behind = await kit.AddAsync("b", "behind it");
        kit.Starter.Refusal = "the checkout failed";
        kit.Starter.RefusedArea = "a";

        var tick = await kit.Pool.TickAsync(None);

        Assert.Equal(new PoolScope("owner/repo", "b"), tick.Scope);
        Assert.NotNull(tick.Worker);
        Assert.Equal(["a", "b"], kit.Starter.Requests.Select(r => r.Scope.Area));
        Assert.Equal(behind.Key, Assert.Single(kit.Starter.Requests[1].Items).Key);
        var refused = Assert.Single(await kit.Pool.ItemsAsync(new PoolScope("owner/repo", "a"), None));
        Assert.Equal((PoolStatus.Queued, "starter"), (refused.Status, refused.Reason));
    }

    [Fact]
    public async Task When_no_scope_starts_the_tick_reports_the_first_ones_hold()
    {
        using var kit = new PoolKit(new FakeCheck("box") { Detail = "memory is short" });
        await kit.AddAsync("a", "first in order", pullRequest: 4);
        await kit.AddAsync("b", "behind it");

        var tick = await kit.Pool.TickAsync(None);

        Assert.Equal((new PoolScope("owner/repo", "a"), "box", "memory is short"), (tick.Scope, tick.Reason, tick.Detail));
        Assert.All(await kit.Pool.ItemsAsync(null, None), i => Assert.Equal("box", i.Reason));
    }

    [Fact]
    public async Task Enqueue_refuses_with_full_once_max_queued_items_wait()
    {
        using var kit = new PoolKit(new TaskPoolSettings(MaxQueued: 2));
        await kit.AddAsync("a", "one");
        await kit.AddAsync("b", "two");

        var third = await kit.Pool.EnqueueAsync(new EnqueueRequest("owner/repo", "c", "three", "body"), None);

        Assert.Null(third.Item);
        Assert.Equal((429, "full"), (third.Refusal!.Status, third.Refusal.Code));
        Assert.Equal(2, (await kit.Pool.ItemsAsync(null, None)).Count);

        await kit.Pool.TickAsync(None);
        var afterClaim = await kit.Pool.EnqueueAsync(new EnqueueRequest("owner/repo", "c", "three", "body"), None);

        Assert.Null(afterClaim.Refusal);
    }

    [Fact]
    public async Task One_start_per_tick()
    {
        using var kit = new PoolKit();
        await kit.AddAsync("a", "one");
        await kit.AddAsync("b", "two");

        await kit.Pool.TickAsync(None);

        Assert.Single(kit.Starter.Requests);
        Assert.Single(await kit.Pool.WorkersAsync(None));
    }

    [Fact]
    public async Task Ceiling_ends_the_worker_with_a_card_and_the_successor_gets_it()
    {
        using var kit = new PoolKit();
        var done = await kit.AddAsync("a", "done");
        var worker = (await kit.Pool.TickAsync(None)).Worker!;
        var left = await kit.AddAsync("a", "left");
        await kit.Pool.ReportAsync(worker, 150_001, None);

        var next = await kit.Pool.NextAsync(worker, None);

        Assert.Equal("ceiling", next.Reason);
        Assert.Empty(next.Items);
        await kit.Pool.DoneAsync([done.Key], null, None);
        Assert.True(await kit.Pool.ReleaseAsync(worker, new ResumeCard("first item", "the late item", "after the first commit"), None));

        var successor = await kit.Pool.TickAsync(None);

        Assert.NotNull(successor.Worker);
        Assert.NotEqual(worker, successor.Worker);
        var request = kit.Starter.Requests[1];
        Assert.NotNull(request.Card);
        Assert.Equal("the late item", request.Card.Left);
        Assert.Equal(worker, request.Card.Worker);
        Assert.Equal(left.Key, Assert.Single(request.Items).Key);
        Assert.Equal(PoolStatus.Done, (await kit.Pool.ItemsAsync(null, None)).Single(i => i.Key == done.Key).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_card_taken_by_a_successor_that_never_ran_goes_to_the_next_one(bool lost)
    {
        using var kit = new PoolKit();
        await kit.AddAsync("a", "item");
        var first = (await kit.Pool.TickAsync(None)).Worker!;
        await kit.Pool.ReleaseAsync(first, new ResumeCard("half", "the rest", "the ceiling"), None);
        if (lost)
        {
            var taker = (await kit.Pool.TickAsync(None)).Worker!;
            Assert.NotNull(kit.Starter.Requests[^1].Card);
            Assert.Equal(1, await kit.Pool.ReleaseLostAsync(new HashSet<string>(), None));
            Assert.NotEqual(first, taker);
        }
        else
        {
            kit.Starter.Refusal = "no room";
            Assert.Null((await kit.Pool.TickAsync(None)).Worker);
            Assert.NotNull(kit.Starter.Requests[^1].Card);
            kit.Starter.Refusal = null;
        }

        Assert.NotNull((await kit.Pool.TickAsync(None)).Worker);

        Assert.Equal("the rest", kit.Starter.Requests[^1].Card?.Left);
    }

    [Fact]
    public async Task Restart_releases_lost_claims_and_starts_nothing_twice()
    {
        using var kit = new PoolKit();
        var finished = await kit.AddAsync("a", "finished");
        var open = await kit.AddAsync("a", "open");
        var w1 = (await kit.Pool.TickAsync(None)).Worker!;
        await kit.Pool.DoneAsync([finished.Key], null, None);

        Assert.Equal(0, await kit.Pool.ReleaseLostAsync(new HashSet<string> { w1 }, None));
        Assert.Equal(PoolStatus.Claimed, (await kit.Pool.ItemsAsync(null, None)).Single(i => i.Key == open.Key).Status);
        Assert.Equal("empty", (await kit.Pool.TickAsync(None)).Reason);
        Assert.Single(kit.Starter.Requests);

        kit.Reopen();
        Assert.Equal(1, await kit.Pool.ReleaseLostAsync(new HashSet<string>(), None));

        var items = await kit.Pool.ItemsAsync(null, None);
        var lost = items.Single(i => i.Key == open.Key);
        Assert.Equal(PoolStatus.Queued, lost.Status);
        Assert.Equal("lost", lost.Reason);
        Assert.Null(lost.Worker);
        Assert.Equal(PoolStatus.Done, items.Single(i => i.Key == finished.Key).Status);
        Assert.Equal("released", (await kit.Pool.NextAsync(w1, None)).Reason);

        await kit.Pool.TickAsync(None);
        Assert.Equal(open.Key, Assert.Single(kit.Starter.Requests[1].Items).Key);
    }

    [Fact]
    public async Task Store_survives_reopen()
    {
        using var kit = new PoolKit();
        var claimed = await kit.AddAsync("a", "claimed");
        var w1 = (await kit.Pool.TickAsync(None)).Worker!;
        await kit.Pool.ReleaseAsync(w1, new ResumeCard("nothing", "all of it", "at the start"), None);
        await kit.Pool.TickAsync(None);
        var queued = await kit.AddAsync("b", "queued", priority: 4);

        kit.Reopen();

        var items = await kit.Pool.ItemsAsync(null, None);
        Assert.Equal([claimed.Key, queued.Key], items.Select(i => i.Key));
        Assert.Equal(PoolStatus.Claimed, items[0].Status);
        Assert.Equal(PoolStatus.Queued, items[1].Status);
        Assert.Equal(4, items[1].Priority);
        Assert.Equal("body of queued", items[1].Body);
        var card = await kit.Pool.CardAsync(new PoolScope("owner/repo", "a"), None);
        Assert.Equal("all of it", card?.Left);
        Assert.Equal(2, (await kit.Pool.WorkersAsync(None)).Count);
    }

    [Fact]
    public async Task Enqueue_refuses_bad_input_with_stable_codes()
    {
        using var kit = new PoolKit();
        var secret = "AKIA" + "ABCDEFGHIJKLMNOP";

        var missing = await kit.Pool.EnqueueAsync(new EnqueueRequest("owner/repo", "a", null, "b"), None);
        var badRepo = await kit.Pool.EnqueueAsync(new EnqueueRequest("not a repo", "a", "t", "b"), None);
        var large = await kit.Pool.EnqueueAsync(new EnqueueRequest("owner/repo", "a", "t", new string('x', EnqueueRequest.MaxBodyBytes + 1)), None);
        var leaked = await kit.Pool.EnqueueAsync(new EnqueueRequest("owner/repo", "a", "t", "key " + secret), None);

        Assert.Equal("invalid", missing.Refusal?.Code);
        Assert.Equal("invalid", badRepo.Refusal?.Code);
        Assert.Equal(413, large.Refusal?.Status);
        Assert.Equal("too_large", large.Refusal?.Code);
        Assert.Equal("secret", leaked.Refusal?.Code);
        Assert.DoesNotContain(secret, leaked.Refusal!.Message, StringComparison.Ordinal);
        Assert.Empty(await kit.Pool.ItemsAsync(null, None));
    }

    [Fact]
    public async Task Batch_stops_at_its_byte_cap_and_the_rest_waits_for_the_next_claim()
    {
        using var kit = new PoolKit(new TaskPoolSettings(MaxBatchBytes: 2500));
        var big = new string('x', 1000);
        var keys = new List<string>();
        for (var i = 0; i < 4; i++)
            keys.Add((await kit.AddAsync("a", "t" + i, body: big)).Key);
        var huge = await kit.AddAsync("b", "huge", body: new string('y', 3000));

        var worker = (await kit.Pool.TickAsync(None)).Worker!;
        var lone = (await kit.Pool.TickAsync(None)).Worker!;

        Assert.Equal(keys.Take(2), kit.Starter.Requests[0].Items.Select(i => i.Key));
        Assert.Equal(huge.Key, Assert.Single(kit.Starter.Requests[1].Items).Key);
        var queued = (await kit.Pool.ItemsAsync(new PoolScope("owner/repo", "a"), None)).Where(i => i.Status == PoolStatus.Queued);
        Assert.Equal(keys.Skip(2), queued.Select(i => i.Key));
        Assert.Equal(keys.Skip(2), (await kit.Pool.NextAsync(worker, None)).Items.Select(i => i.Key));
        Assert.Empty((await kit.Pool.NextAsync(lone, None)).Items);
    }

    [Fact]
    public void Batch_cap_must_be_positive()
    {
        Assert.Contains("max_batch_bytes must be a positive number of bytes", new TaskPoolSettings(MaxBatchBytes: 0).Problems());
        Assert.Contains("max_queued must be a positive number of items", new TaskPoolSettings(MaxQueued: 0).Problems());
    }
}
