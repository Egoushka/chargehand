namespace Chargehand.Modules.Pool.Api;

public sealed partial class SqlitePoolStore
{
    /// <summary>The first <paramref name="limit"/> queued items in enqueue order; no body is read.</summary>
    public Task<IReadOnlyList<PoolQueuedRow>> QueuedAsync(int limit, CancellationToken ct)
    {
        using var db = Connect(connection);
        using var cmd = Command(db, null, """
            SELECT key, repository, area, title, priority, pull_request, enqueued_at, reason FROM items
            WHERE status = 'queued' ORDER BY enqueued_at, rowid LIMIT $limit;
            """, ("$limit", limit));
        using var reader = cmd.ExecuteReader();
        var rows = new List<PoolQueuedRow>();
        while (reader.Read())
            rows.Add(new PoolQueuedRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5), Parse(reader.GetString(6)), reader.IsDBNull(7) ? null : reader.GetString(7)));
        return Task.FromResult<IReadOnlyList<PoolQueuedRow>>(rows);
    }

    /// <summary>The newest <paramref name="limit"/> items finished at or after <paramref name="since"/>, oldest first; no body is read.</summary>
    public Task<IReadOnlyList<PoolDoneRow>> DoneSinceAsync(DateTimeOffset since, int limit, CancellationToken ct)
    {
        using var db = Connect(connection);
        using var cmd = Command(db, null, """
            SELECT key, repository, area, title, done_at FROM items
            WHERE status = 'done' AND done_at >= $since ORDER BY done_at DESC, rowid DESC LIMIT $limit;
            """, ("$since", Stamp(since)), ("$limit", limit));
        using var reader = cmd.ExecuteReader();
        var rows = new List<PoolDoneRow>();
        while (reader.Read())
            rows.Add(new PoolDoneRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Parse(reader.GetString(4))));
        rows.Reverse();
        return Task.FromResult<IReadOnlyList<PoolDoneRow>>(rows);
    }

    /// <summary>The first <paramref name="limit"/> unreleased workers in start order, each with the count of items it holds.</summary>
    public Task<IReadOnlyList<PoolLiveWorker>> LiveWorkersAsync(int limit, CancellationToken ct)
    {
        using var db = Connect(connection);
        using var cmd = Command(db, null, """
            SELECT w.id, w.repository, w.area, w.tokens, w.started_at, COUNT(i.key) FROM workers w
            LEFT JOIN items i ON i.worker = w.id AND i.status = 'claimed'
            WHERE w.released_at IS NULL GROUP BY w.id ORDER BY w.started_at, w.rowid LIMIT $limit;
            """, ("$limit", limit));
        using var reader = cmd.ExecuteReader();
        var rows = new List<PoolLiveWorker>();
        while (reader.Read())
            rows.Add(new PoolLiveWorker(reader.GetString(0), new PoolScope(reader.GetString(1), reader.GetString(2)), reader.GetInt64(3),
                Parse(reader.GetString(4)), reader.GetInt32(5)));
        return Task.FromResult<IReadOnlyList<PoolLiveWorker>>(rows);
    }
}
