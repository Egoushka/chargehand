using System.Net;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Pool.Api;

namespace Chargehand.Tests;

/// <summary>The owner-only <c>pool</c> section of <c>GET /v1/overview</c> (D-155).</summary>
public class ConsoleOverviewPoolTests
{
    private static AccountSettings Alice() => new("alice", 5m, Driven: true, AllowServerCredential: true);

    private static readonly TaskPoolSettings PoolOn = new(WorkerTokens: 1000, MaxOpenDecisions: 3);

    private sealed class AcceptingStarter : IPoolStarter
    {
        public Task<string?> StartAsync(PoolStartRequest request, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    /// <summary>A second handle on the server's pool file with a starter that accepts, so a test can leave workers, ceiling holds and done items behind.</summary>
    private static (TaskPool Pool, SqlitePoolStore Store) SeedPool(ConsoleRig rig)
    {
        var store = SqlitePoolStore.Open(Path.Combine(rig.Root, "pool.db"));
        return (new TaskPool(store, PoolOn, [], new AcceptingStarter(), rig.Clock), store);
    }

    private static Task<EnqueueResult> Add(TaskPool pool, string area, string title, int priority = 0, int? pr = null) =>
        pool.EnqueueAsync(new EnqueueRequest("o/r", area, title, "SECRET-BODY " + title, priority, pr), CancellationToken.None);

    [Fact]
    public async Task The_owners_overview_pool_lists_queued_workers_and_done_without_any_body()
    {
        await using var rig = await ConsoleRig.StartAsync(pool: dir => PoolOn with { Database = Path.Combine(dir, "pool.db") });
        var (pool, store) = SeedPool(rig);
        using var handle = store;
        await Add(pool, "a", "one");
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await Add(pool, "b", "two", priority: 2, pr: 7);
        var worker = (await pool.TickAsync(CancellationToken.None)).Worker!;
        Assert.NotNull(worker);

        var text = await ConsoleRig.Text(rig.Owner, "/v1/overview");
        var p = JsonDocument.Parse(text).RootElement.GetProperty("pool");

        Assert.Equal((1000, 3), (p.GetProperty("ceiling").GetProperty("worker_tokens").GetInt32(), p.GetProperty("ceiling").GetProperty("max_open_decisions").GetInt32()));
        var live = Assert.Single(p.GetProperty("workers").EnumerateArray());
        Assert.Equal((worker, "o/r", 0, 1), (live.GetProperty("id").GetString(), live.GetProperty("repository").GetString(), live.GetProperty("tokens").GetInt32(), live.GetProperty("items").GetInt32()));
        Assert.True(live.TryGetProperty("started_at", out _));
        var queued = Assert.Single(p.GetProperty("queued").EnumerateArray());
        Assert.Equal(("o/r", "a", "one", 0), (queued.GetProperty("repository").GetString(), queued.GetProperty("area").GetString(), queued.GetProperty("title").GetString(), queued.GetProperty("priority").GetInt32()));
        Assert.False(queued.TryGetProperty("pull_request", out _));
        Assert.True(queued.TryGetProperty("enqueued_at", out _));
        Assert.Empty(p.GetProperty("done").EnumerateArray());
        Assert.DoesNotContain("SECRET-BODY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"body\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_pool_gives_empty_lists_and_the_ceiling()
    {
        await using var rig = await ConsoleRig.StartAsync(pool: dir => PoolOn with { Database = Path.Combine(dir, "pool.db") });

        var p = (await ConsoleRig.Get(rig.Owner, "/v1/overview")).GetProperty("pool");

        Assert.Equal(1000, p.GetProperty("ceiling").GetProperty("worker_tokens").GetInt32());
        foreach (var list in new[] { "queued", "workers", "done" })
            Assert.Equal(0, p.GetProperty(list).GetArrayLength());
    }

    [Fact]
    public async Task The_overview_has_no_pool_for_an_account_key_or_a_profile_without_http_pool()
    {
        await using var withPool = await ConsoleRig.StartAsync(accounts: [Alice()], pool: dir => PoolOn with { Database = Path.Combine(dir, "pool.db") });
        await using var without = await ConsoleRig.StartAsync();

        Assert.False((await ConsoleRig.Get(withPool.As("alice"), "/v1/overview")).TryGetProperty("pool", out _));
        Assert.False((await ConsoleRig.Get(without.Owner, "/v1/overview")).TryGetProperty("pool", out _));
    }

    [Fact]
    public async Task An_item_the_ceiling_released_shows_reason_ceiling_and_since_limits_done()
    {
        await using var rig = await ConsoleRig.StartAsync(pool: dir => PoolOn with { Database = Path.Combine(dir, "pool.db") });
        var (pool, store) = SeedPool(rig);
        using var handle = store;
        var old = (await Add(pool, "a", "old")).Item!.Item;
        await pool.DoneAsync([old.Key], null, CancellationToken.None);
        rig.Clock.Advance(TimeSpan.FromHours(30));
        var recent = (await Add(pool, "a", "recent")).Item!.Item;
        await pool.DoneAsync([recent.Key], null, CancellationToken.None);
        await Add(pool, "b", "held");
        var worker = (await pool.TickAsync(CancellationToken.None)).Worker!;
        await pool.ReportAsync(worker, 1001, CancellationToken.None);
        Assert.True(await pool.ReleaseAsync(worker, new ResumeCard("d", "l", "s"), CancellationToken.None));

        var day = (await ConsoleRig.Get(rig.Owner, "/v1/overview")).GetProperty("pool");
        var since = Uri.EscapeDataString(ConsoleRig.Now.AddDays(-1).AddHours(40).ToString("o"));
        var all = Uri.EscapeDataString(ConsoleRig.Now.AddDays(-5).ToString("o"));
        var narrow = (await ConsoleRig.Get(rig.Owner, "/v1/overview?since=" + since)).GetProperty("pool");
        var wide = (await ConsoleRig.Get(rig.Owner, "/v1/overview?since=" + all)).GetProperty("pool");

        Assert.Equal("ceiling", Assert.Single(day.GetProperty("queued").EnumerateArray()).GetProperty("reason").GetString());
        Assert.Equal(["recent"], day.GetProperty("done").EnumerateArray().Select(d => d.GetProperty("title").GetString()));
        Assert.Equal(["recent"], narrow.GetProperty("done").EnumerateArray().Select(d => d.GetProperty("title").GetString()));
        Assert.Equal(["old", "recent"], wide.GetProperty("done").EnumerateArray().Select(d => d.GetProperty("title").GetString()));
        Assert.Equal(0, day.GetProperty("workers").GetArrayLength());
    }

    [Fact]
    public async Task The_pool_lists_are_capped_at_fifty_oldest_first()
    {
        await using var rig = await ConsoleRig.StartAsync(pool: dir => PoolOn with { Database = Path.Combine(dir, "pool.db") });
        var (pool, store) = SeedPool(rig);
        using var handle = store;
        for (var i = 0; i < 55; i++)
        {
            await Add(pool, "a", $"t{i:00}");
            rig.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var queued = (await ConsoleRig.Get(rig.Owner, "/v1/overview")).GetProperty("pool").GetProperty("queued").EnumerateArray().ToList();

        Assert.Equal(50, queued.Count);
        Assert.Equal(("t00", "t49"), (queued[0].GetProperty("title").GetString(), queued[^1].GetProperty("title").GetString()));
    }

    [Fact]
    public async Task A_since_that_is_not_a_time_is_a_400_and_no_since_is_a_200()
    {
        await using var rig = await ConsoleRig.StartAsync(pool: dir => PoolOn with { Database = Path.Combine(dir, "pool.db") });

        Assert.Equal(HttpStatusCode.BadRequest, await ConsoleRig.Status(rig.Owner, "/v1/overview?since=junk"));
        Assert.Equal(HttpStatusCode.OK, await ConsoleRig.Status(rig.Owner, "/v1/overview"));
    }

    [Fact]
    public async Task A_done_window_over_the_cap_keeps_the_newest_fifty_oldest_first()
    {
        await using var rig = await ConsoleRig.StartAsync(pool: dir => PoolOn with { Database = Path.Combine(dir, "pool.db") });
        var (pool, store) = SeedPool(rig);
        using var handle = store;
        for (var i = 0; i < 55; i++)
        {
            await pool.DoneAsync([(await Add(pool, "a", $"d{i:00}")).Item!.Item.Key], null, CancellationToken.None);
            rig.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var done = (await ConsoleRig.Get(rig.Owner, "/v1/overview")).GetProperty("pool").GetProperty("done").EnumerateArray().ToList();

        Assert.Equal(50, done.Count);
        Assert.Equal(("d05", "d54"), (done[0].GetProperty("title").GetString(), done[^1].GetProperty("title").GetString()));
    }

    [Fact]
    public async Task Titles_in_the_overview_pool_are_scrubbed()
    {
        await using var rig = await ConsoleRig.StartAsync(pool: dir => PoolOn with { Database = Path.Combine(dir, "pool.db") });
        var (pool, store) = SeedPool(rig);
        using var handle = store;
        var token = "ghp_" + new string('a', 36);
        await Add(pool, "a", "fix " + token);

        var text = await ConsoleRig.Text(rig.Owner, "/v1/overview");

        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
    }
}
