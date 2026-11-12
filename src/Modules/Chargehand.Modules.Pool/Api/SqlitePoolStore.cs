using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Chargehand.Modules.Pool.Api;

/// <summary>The items a new worker claimed and the card its predecessor left.</summary>
public sealed record PoolClaim(IReadOnlyList<PoolItem> Items, ResumeCard? Card);

/// <summary>The pool's rows in one SQLite file: items, workers and resume cards (ADR 0099).</summary>
public sealed class SqlitePoolStore : IDisposable
{
    public const int Version = 1;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS items (
            key TEXT PRIMARY KEY,
            source TEXT NOT NULL,
            repository TEXT NOT NULL,
            area TEXT NOT NULL,
            title TEXT NOT NULL,
            body TEXT NOT NULL,
            priority INTEGER NOT NULL,
            pull_request INTEGER NULL,
            enqueued_at TEXT NOT NULL,
            status TEXT NOT NULL,
            worker TEXT NULL,
            reason TEXT NULL,
            claimed_at TEXT NULL,
            done_at TEXT NULL);
        CREATE INDEX IF NOT EXISTS items_scope ON items (repository, area, status);
        CREATE INDEX IF NOT EXISTS items_worker ON items (worker);
        CREATE TABLE IF NOT EXISTS workers (
            id TEXT PRIMARY KEY,
            repository TEXT NOT NULL,
            area TEXT NOT NULL,
            started_at TEXT NOT NULL,
            tokens INTEGER NOT NULL DEFAULT 0,
            released_at TEXT NULL,
            reason TEXT NULL);
        CREATE INDEX IF NOT EXISTS workers_scope ON workers (repository, area, released_at);
        CREATE TABLE IF NOT EXISTS cards (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            repository TEXT NOT NULL,
            area TEXT NOT NULL,
            worker TEXT NOT NULL,
            done_note TEXT NOT NULL,
            left_note TEXT NOT NULL,
            stopped_note TEXT NOT NULL,
            written_at TEXT NOT NULL,
            taken_by TEXT NULL);

        """;

    private const string Columns =
        "key, source, repository, area, title, body, priority, pull_request, enqueued_at, status, worker, reason, claimed_at, done_at";

    private readonly string connection;
    private readonly SemaphoreSlim writer = new(1, 1);

    private SqlitePoolStore(string connection) => this.connection = connection;

    /// <summary>Opens or creates the file; refuses a file written by a newer version.</summary>
    public static SqlitePoolStore Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var db = Connect(cs);
        var version = Convert.ToInt32(Scalar(db, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version > Version)
            throw new InvalidOperationException($"pool store version {version} is newer than this build ({Version})");
        Execute(db, "PRAGMA journal_mode=WAL;");
        if (version < Version)
            Execute(db, Schema + $"PRAGMA user_version={Version};");
        return new SqlitePoolStore(cs);
    }

    public void Dispose()
    {
        writer.Dispose();
        using var pooled = new SqliteConnection(connection);
        SqliteConnection.ClearPool(pooled);
    }

    /// <summary>Writes one queued item, or nothing (null) when <paramref name="maxQueued"/> items already wait; the request must already be valid.</summary>
    public Task<Enqueued?> EnqueueAsync(EnqueueRequest request, int maxQueued, DateTimeOffset now, CancellationToken ct) =>
        WriteAsync<Enqueued?>((db, tx) =>
        {
            using (var queued = Command(db, tx, "SELECT COUNT(*) FROM items WHERE status = 'queued';"))
            {
                if (Convert.ToInt64(queued.ExecuteScalar(), CultureInfo.InvariantCulture) >= maxQueued)
                    return null;
            }
            var key = "p-" + Guid.NewGuid().ToString("N");
            Command(db, tx, $"INSERT INTO items ({Columns}) VALUES ($key, $source, $repo, $area, $title, $body, $priority, $pr, $at, 'queued', NULL, NULL, NULL, NULL);",
                ("$key", key), ("$source", request.Source), ("$repo", request.Repository), ("$area", request.Area),
                ("$title", request.Title), ("$body", request.Body), ("$priority", request.Priority),
                ("$pr", request.PullRequest), ("$at", Stamp(now))).ExecuteNonQuery();
            using var count = Command(db, tx, "SELECT COUNT(*) FROM items WHERE repository = $repo AND area = $area AND status = 'queued';",
                ("$repo", request.Repository), ("$area", request.Area));
            var position = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
            var item = new PoolItem(key, request.Source, request.Repository!, request.Area!, request.Title!, request.Body!,
                request.Priority, request.PullRequest, now, PoolStatus.Queued, null, null, null, null);
            return new Enqueued(item, position);
        }, ct);

    /// <summary>Scopes with queued items and no live worker: pull requests first, then priority, then the oldest item.</summary>
    public Task<IReadOnlyList<PoolScope>> StartableAsync(CancellationToken ct)
    {
        using var db = Connect(connection);
        using var cmd = Command(db, null, """
            SELECT i.repository, i.area FROM items i
            WHERE i.status = 'queued' AND NOT EXISTS (
                SELECT 1 FROM workers w WHERE w.repository = i.repository AND w.area = i.area AND w.released_at IS NULL)
            GROUP BY i.repository, i.area
            ORDER BY MAX(i.pull_request IS NOT NULL) DESC, MAX(i.priority) DESC, MIN(i.enqueued_at), MIN(i.rowid);
            """);
        using var reader = cmd.ExecuteReader();
        var scopes = new List<PoolScope>();
        while (reader.Read())
            scopes.Add(new PoolScope(reader.GetString(0), reader.GetString(1)));
        return Task.FromResult<IReadOnlyList<PoolScope>>(scopes);
    }

    /// <summary>Records why the scope did not start on every one of its queued items.</summary>
    public Task HoldAsync(PoolScope scope, string reason, CancellationToken ct) =>
        WriteAsync((db, tx) =>
        {
            using var cmd = Command(db, tx, "UPDATE items SET reason = $reason WHERE repository = $repo AND area = $area AND status = 'queued';",
                ("$reason", reason), ("$repo", scope.Repository), ("$area", scope.Area));
            return cmd.ExecuteNonQuery();
        }, ct);

    /// <summary>Starts a worker for the scope, claims queued items oldest first up to <paramref name="maxBytes"/> of title and body (always at least one) and
    /// takes the latest untaken card.</summary>
    public Task<PoolClaim> StartWorkerAsync(PoolScope scope, string worker, long maxBytes, DateTimeOffset now, CancellationToken ct) =>
        WriteAsync((db, tx) =>
        {
            using (var live = Command(db, tx, "SELECT COUNT(*) FROM workers WHERE repository = $repo AND area = $area AND released_at IS NULL;",
                ("$repo", scope.Repository), ("$area", scope.Area)))
            {
                if (Convert.ToInt64(live.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                    throw new InvalidOperationException("the scope already has a live worker");
            }
            using (var insert = Command(db, tx, "INSERT INTO workers (id, repository, area, started_at) VALUES ($id, $repo, $area, $at);",
                ("$id", worker), ("$repo", scope.Repository), ("$area", scope.Area), ("$at", Stamp(now))))
                insert.ExecuteNonQuery();
            var items = ClaimQueued(db, tx, scope, worker, maxBytes, now);
            ResumeCard? card = null;
            long? cardId = null;
            using (var read = Command(db, tx, """
                SELECT id, done_note, left_note, stopped_note, worker, written_at FROM cards
                WHERE repository = $repo AND area = $area AND taken_by IS NULL ORDER BY id DESC LIMIT 1;
                """, ("$repo", scope.Repository), ("$area", scope.Area)))
            using (var reader = read.ExecuteReader())
            {
                if (reader.Read())
                {
                    cardId = reader.GetInt64(0);
                    card = new ResumeCard(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), Parse(reader.GetString(5)));
                }
            }
            if (cardId is { } id)
            {
                using var take = Command(db, tx, "UPDATE cards SET taken_by = $worker WHERE id = $id;", ("$worker", worker), ("$id", id));
                take.ExecuteNonQuery();
            }
            return new PoolClaim(items, card);
        }, ct);

    /// <summary>Claims the queued items of a live worker's scope up to <paramref name="maxBytes"/>; nothing for a released or unknown worker.</summary>
    public Task<IReadOnlyList<PoolItem>> ClaimAsync(string worker, long maxBytes, DateTimeOffset now, CancellationToken ct) =>
        WriteAsync<IReadOnlyList<PoolItem>>((db, tx) =>
        {
            var found = ReadWorkers(db, tx, "WHERE id = $id", ("$id", worker));
            if (found.Count == 0 || found[0].ReleasedAt is not null)
                return [];
            return ClaimQueued(db, tx, found[0].Scope, worker, maxBytes, now);
        }, ct);

    public Task<PoolWorker?> WorkerAsync(string worker, CancellationToken ct)
    {
        using var db = Connect(connection);
        var found = ReadWorkers(db, null, "WHERE id = $id", ("$id", worker));
        return Task.FromResult(found.Count == 0 ? null : found[0]);
    }

    /// <summary>Every worker, live ones included, in start order.</summary>
    public Task<IReadOnlyList<PoolWorker>> WorkersAsync(CancellationToken ct)
    {
        using var db = Connect(connection);
        return Task.FromResult<IReadOnlyList<PoolWorker>>(ReadWorkers(db, null, ""));
    }

    /// <summary>Stores the worker's total tokens so far (an absolute value, not a delta).</summary>
    public Task ReportAsync(string worker, long tokens, CancellationToken ct) =>
        WriteAsync((db, tx) =>
        {
            using var cmd = Command(db, tx, "UPDATE workers SET tokens = $tokens WHERE id = $id;", ("$tokens", tokens), ("$id", worker));
            return cmd.ExecuteNonQuery();
        }, ct);

    /// <summary>
    /// Ends a live worker: its claimed items go back to the queue with <paramref name="reason"/>, its card (if any) is
    /// written for the successor. A worker lost or refused by the starter gives back the card it took.
    /// </summary>
    public Task<bool> ReleaseAsync(string worker, ResumeCard? card, string reason, DateTimeOffset now, CancellationToken ct) =>
        WriteAsync((db, tx) => Release(db, tx, worker, card, reason, now), ct);

    /// <summary>Marks the items done; <paramref name="reason"/> says why when they did not succeed.</summary>
    public Task DoneAsync(IReadOnlyList<string> keys, string? reason, DateTimeOffset now, CancellationToken ct) =>
        WriteAsync((db, tx) =>
        {
            foreach (var key in keys)
            {
                using var cmd = Command(db, tx, "UPDATE items SET status = 'done', reason = $reason, done_at = $at WHERE key = $key AND status <> 'done';",
                    ("$reason", reason), ("$at", Stamp(now)), ("$key", key));
                cmd.ExecuteNonQuery();
            }
            return keys.Count;
        }, ct);

    /// <summary>Releases every live worker not in <paramref name="live"/> with reason <c>lost</c>; returns how many.</summary>
    public Task<int> ReleaseLostAsync(IReadOnlySet<string> live, DateTimeOffset now, CancellationToken ct) =>
        WriteAsync((db, tx) =>
        {
            var lost = ReadWorkers(db, tx, "WHERE released_at IS NULL").Where(w => !live.Contains(w.Id)).ToList();
            foreach (var w in lost)
                Release(db, tx, w.Id, null, "lost", now);
            return lost.Count;
        }, ct);

    /// <summary>Every item, or the scope's, in enqueue order.</summary>
    public Task<IReadOnlyList<PoolItem>> ItemsAsync(PoolScope? scope, CancellationToken ct)
    {
        using var db = Connect(connection);
        using var cmd = scope is null
            ? Command(db, null, $"SELECT {Columns} FROM items ORDER BY enqueued_at, rowid;")
            : Command(db, null, $"SELECT {Columns} FROM items WHERE repository = $repo AND area = $area ORDER BY enqueued_at, rowid;",
                ("$repo", scope.Repository), ("$area", scope.Area));
        return Task.FromResult<IReadOnlyList<PoolItem>>(ReadItems(cmd));
    }

    /// <summary>The scope's latest card, taken or not.</summary>
    public Task<ResumeCard?> CardAsync(PoolScope scope, CancellationToken ct)
    {
        using var db = Connect(connection);
        using var cmd = Command(db, null, """
            SELECT done_note, left_note, stopped_note, worker, written_at FROM cards
            WHERE repository = $repo AND area = $area ORDER BY id DESC LIMIT 1;
            """, ("$repo", scope.Repository), ("$area", scope.Area));
        using var reader = cmd.ExecuteReader();
        ResumeCard? card = reader.Read()
            ? new ResumeCard(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Parse(reader.GetString(4)))
            : null;
        return Task.FromResult(card);
    }

    private static bool Release(SqliteConnection db, SqliteTransaction tx, string worker, ResumeCard? card, string reason, DateTimeOffset now)
    {
        var found = ReadWorkers(db, tx, "WHERE id = $id", ("$id", worker));
        if (found.Count == 0 || found[0].ReleasedAt is not null)
            return false;
        var scope = found[0].Scope;
        var at = Stamp(now);
        using (var end = Command(db, tx, "UPDATE workers SET released_at = $at, reason = $reason WHERE id = $id;",
            ("$at", at), ("$reason", reason), ("$id", worker)))
            end.ExecuteNonQuery();
        using (var requeue = Command(db, tx, "UPDATE items SET status = 'queued', worker = NULL, claimed_at = NULL, reason = $reason WHERE worker = $id AND status = 'claimed';",
            ("$reason", reason), ("$id", worker)))
            requeue.ExecuteNonQuery();
        if (card is not null)
        {
            using var write = Command(db, tx, """
                INSERT INTO cards (repository, area, worker, done_note, left_note, stopped_note, written_at)
                VALUES ($repo, $area, $worker, $done, $left, $stopped, $at);
                """, ("$repo", scope.Repository), ("$area", scope.Area), ("$worker", worker),
                ("$done", card.Done), ("$left", card.Left), ("$stopped", card.Stopped), ("$at", at));
            write.ExecuteNonQuery();
        }
        if (reason is "starter" or "lost")
        {
            using var giveBack = Command(db, tx, "UPDATE cards SET taken_by = NULL WHERE taken_by = $id;", ("$id", worker));
            giveBack.ExecuteNonQuery();
        }
        return true;
    }

    private static List<PoolItem> ClaimQueued(SqliteConnection db, SqliteTransaction tx, PoolScope scope, string worker, long maxBytes, DateTimeOffset now)
    {
        List<PoolItem> queued;
        using (var select = Command(db, tx, $"SELECT {Columns} FROM items WHERE repository = $repo AND area = $area AND status = 'queued' ORDER BY enqueued_at, rowid;",
            ("$repo", scope.Repository), ("$area", scope.Area)))
            queued = ReadItems(select);
        long bytes = 0;
        queued = queued.TakeWhile((item, i) => (bytes += Encoding.UTF8.GetByteCount(item.Title) + Encoding.UTF8.GetByteCount(item.Body)) <= maxBytes || i == 0).ToList();
        foreach (var item in queued)
        {
            using var claim = Command(db, tx, "UPDATE items SET status = 'claimed', worker = $worker, reason = NULL, claimed_at = $at WHERE key = $key;",
                ("$worker", worker), ("$at", Stamp(now)), ("$key", item.Key));
            claim.ExecuteNonQuery();
        }
        return queued.Select(i => i with { Status = PoolStatus.Claimed, Worker = worker, Reason = null, ClaimedAt = now }).ToList();
    }

    private static List<PoolWorker> ReadWorkers(SqliteConnection db, SqliteTransaction? tx, string where, params (string, object?)[] args)
    {
        using var cmd = Command(db, tx, $"SELECT id, repository, area, tokens, started_at, released_at, reason FROM workers {where} ORDER BY started_at, rowid;", args);
        using var reader = cmd.ExecuteReader();
        var workers = new List<PoolWorker>();
        while (reader.Read())
            workers.Add(new PoolWorker(reader.GetString(0), new PoolScope(reader.GetString(1), reader.GetString(2)), reader.GetInt64(3),
                Parse(reader.GetString(4)), reader.IsDBNull(5) ? null : Parse(reader.GetString(5)), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return workers;
    }

    private static List<PoolItem> ReadItems(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var items = new List<PoolItem>();
        while (reader.Read())
            items.Add(new PoolItem(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetInt32(7), Parse(reader.GetString(8)), ParseStatus(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : Parse(reader.GetString(12)), reader.IsDBNull(13) ? null : Parse(reader.GetString(13))));
        return items;
    }

    private static PoolStatus ParseStatus(string s) => s switch
    {
        "queued" => PoolStatus.Queued,
        "claimed" => PoolStatus.Claimed,
        "done" => PoolStatus.Done,
        _ => throw new InvalidOperationException($"unknown item status '{s}'"),
    };

    private async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> work, CancellationToken ct)
    {
        await writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = Connect(connection);
            using var tx = db.BeginTransaction();
            var result = work(db, tx);
            tx.Commit();
            return result;
        }
        finally
        {
            writer.Release();
        }
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static string Stamp(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static SqliteConnection Connect(string cs)
    {
        var db = new SqliteConnection(cs);
        db.Open();
        Execute(db, "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;");
        return db;
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
