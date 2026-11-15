using Chargehand.Modules.Pool.Api;
using Microsoft.Data.Sqlite;

namespace Chargehand.Modules.Pool.Tests;

public sealed class PoolIssueSizeTests
{
    [Fact]
    public async Task An_old_pool_db_migrates_and_keeps_its_items()
    {
        var dir = Directory.CreateTempSubdirectory("chargehand-pool-").FullName;
        var path = Path.Combine(dir, "pool.db");
        try
        {
            using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                raw.Open();
                using var cmd = raw.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE items (key TEXT PRIMARY KEY, source TEXT NOT NULL, repository TEXT NOT NULL, area TEXT NOT NULL, title TEXT NOT NULL,
                        body TEXT NOT NULL, priority INTEGER NOT NULL, pull_request INTEGER NULL, enqueued_at TEXT NOT NULL, status TEXT NOT NULL,
                        worker TEXT NULL, reason TEXT NULL, claimed_at TEXT NULL, done_at TEXT NULL);
                    CREATE TABLE workers (id TEXT PRIMARY KEY, repository TEXT NOT NULL, area TEXT NOT NULL, started_at TEXT NOT NULL,
                        tokens INTEGER NOT NULL DEFAULT 0, released_at TEXT NULL, reason TEXT NULL);
                    CREATE TABLE cards (id INTEGER PRIMARY KEY AUTOINCREMENT, repository TEXT NOT NULL, area TEXT NOT NULL, worker TEXT NOT NULL,
                        done_note TEXT NOT NULL, left_note TEXT NOT NULL, stopped_note TEXT NOT NULL, written_at TEXT NOT NULL, taken_by TEXT NULL);
                    INSERT INTO items VALUES ('p-old', 'enqueue', 'owner/app', 'docs', 'Old', 'b', 0, NULL, '2026-01-01T00:00:00.0000000+00:00', 'queued', NULL, NULL, NULL, NULL);
                    PRAGMA user_version=1;
                    """;
                cmd.ExecuteNonQuery();
            }
            using var store = SqlitePoolStore.Open(path);
            var item = Assert.Single(await store.ItemsAsync(null, CancellationToken.None));
            Assert.Equal("Old", item.Title);
            Assert.Null(item.Issue);
            Assert.Null(item.Size);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Has_open_item_for_issue()
    {
        using var kit = new PoolKit();
        var item = await kit.AddAsync("docs", "Fix it", issue: 7, size: "L");
        Assert.Equal(7, item.Issue);
        Assert.Equal("L", item.Size);
        Assert.True(await kit.Pool.HasOpenItemAsync("owner/repo", 7, CancellationToken.None));
        Assert.False(await kit.Pool.HasOpenItemAsync("owner/repo", 8, CancellationToken.None));
        Assert.False(await kit.Pool.HasOpenItemAsync("owner/other", 7, CancellationToken.None));
        await kit.Pool.DoneAsync([item.Key], null, CancellationToken.None);
        Assert.False(await kit.Pool.HasOpenItemAsync("owner/repo", 7, CancellationToken.None));
    }

    [Fact]
    public void A_size_outside_s_m_l_is_refused()
    {
        Assert.Equal("invalid", new EnqueueRequest("owner/repo", "a", "t", "b", Size: "XL").Validate()?.Code);
        Assert.Null(new EnqueueRequest("owner/repo", "a", "t", "b", Size: "M", Issue: 3).Validate());
        Assert.Equal("invalid", new EnqueueRequest("owner/repo", "a", "t", "b", Issue: 0).Validate()?.Code);
    }
}
