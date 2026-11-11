using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Chargehand.Modules.Events.Api;

/// <summary><see cref="IEventLog"/> on one SQLite file (<c>events.db</c>): WAL, <c>synchronous=FULL</c>, one transaction and one flush per batch (events spec, decision 1).</summary>
public sealed class SqliteEventLog : IEventLog, IDisposable
{
    /// <summary>The schema version in <c>PRAGMA user_version</c>.</summary>
    public const int Version = 1;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS events (
            seq INTEGER PRIMARY KEY AUTOINCREMENT,
            source TEXT NOT NULL,
            id TEXT NOT NULL,
            type TEXT NOT NULL,
            subject TEXT,
            scope TEXT NOT NULL,
            correlationid TEXT,
            time TEXT NOT NULL,
            envelope TEXT NOT NULL,
            UNIQUE (source, id));
        CREATE INDEX IF NOT EXISTS events_type ON events (type, seq);
        CREATE INDEX IF NOT EXISTS events_scope ON events (scope, seq);
        CREATE INDEX IF NOT EXISTS events_subject ON events (subject, seq) WHERE subject IS NOT NULL;
        CREATE INDEX IF NOT EXISTS events_correlation ON events (correlationid, seq) WHERE correlationid IS NOT NULL;
        """;

    private readonly string connection;
    private readonly SemaphoreSlim writer = new(1, 1);

    private SqliteEventLog(string connection, long latest)
    {
        this.connection = connection;
        Signal = new EventSignal(latest);
    }

    public EventSignal Signal { get; }

    public void Dispose()
    {
        writer.Dispose();
        using var pooled = new SqliteConnection(connection);
        SqliteConnection.ClearPool(pooled);
    }

    /// <summary>Opens the file, creating it and its schema when absent. A file with a newer schema than this build knows is refused.</summary>
    public static SqliteEventLog Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } dir)
            Directory.CreateDirectory(dir);
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var db = Connect(cs);
        var version = Convert.ToInt32(Scalar(db, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version > Version)
            throw new InvalidOperationException($"{path} has event-log schema {version}; this build knows up to {Version}");
        Execute(db, "PRAGMA journal_mode=WAL;");
        if (version < Version)
            Execute(db, Schema + $"PRAGMA user_version={Version};");
        var latest = Scalar(db, "SELECT COALESCE(MAX(seq), 0) FROM events;");
        return new SqliteEventLog(cs, Convert.ToInt64(latest, CultureInfo.InvariantCulture));
    }

    public async Task<IReadOnlyList<long>> AppendAsync(IReadOnlyList<NewEvent> batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
            return [];
        var seqs = new List<long>(batch.Count);
        await writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = Connect(connection);
            using var tx = db.BeginTransaction();
            foreach (var e in batch)
                seqs.Add(Insert(db, tx, e));
            tx.Commit();
        }
        finally
        {
            writer.Release();
        }
        Signal.Notify(seqs.Max());
        return seqs;
    }

    public Task<IReadOnlyList<StoredEvent>> ReadAsync(EventFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        using var db = Connect(connection);
        using var cmd = db.CreateCommand();
        cmd.CommandText = Select(filter, cmd);
        var found = new List<StoredEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            found.Add(new StoredEvent(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), r.GetString(8)));
        }
        return Task.FromResult<IReadOnlyList<StoredEvent>>(found);
    }

    private static long Insert(SqliteConnection db, SqliteTransaction tx, NewEvent e)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO events (source, id, type, subject, scope, correlationid, time, envelope)
            VALUES ($source, $id, $type, $subject, $scope, $correlation, $time, $envelope)
            ON CONFLICT (source, id) DO NOTHING RETURNING seq;
            """;
        cmd.Parameters.AddWithValue("$source", e.Source);
        cmd.Parameters.AddWithValue("$id", e.Id);
        cmd.Parameters.AddWithValue("$type", e.Type);
        cmd.Parameters.AddWithValue("$subject", (object?)e.Subject ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$scope", e.Scope);
        cmd.Parameters.AddWithValue("$correlation", (object?)e.CorrelationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$time", e.Time);
        cmd.Parameters.AddWithValue("$envelope", e.Envelope);
        if (cmd.ExecuteScalar() is long seq)
            return seq;
        using var first = db.CreateCommand();
        first.Transaction = tx;
        first.CommandText = "SELECT seq FROM events WHERE source = $source AND id = $id;";
        first.Parameters.AddWithValue("$source", e.Source);
        first.Parameters.AddWithValue("$id", e.Id);
        return Convert.ToInt64(first.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string Select(EventFilter f, SqliteCommand cmd)
    {
        var sql = new StringBuilder("SELECT seq, source, id, type, subject, scope, correlationid, time, envelope FROM events WHERE seq > $after");
        cmd.Parameters.AddWithValue("$after", f.After);
        if (f.UpTo is { } upTo)
        {
            sql.Append(" AND seq <= $upto");
            cmd.Parameters.AddWithValue("$upto", upTo);
        }
        Match(sql, cmd, "type", f.Type);
        Match(sql, cmd, "source", f.Source);
        Match(sql, cmd, "scope", f.Scope);
        if (f.Subject is { } subject)
        {
            sql.Append(" AND subject = $subject");
            cmd.Parameters.AddWithValue("$subject", subject);
        }
        if (f.Since is { } since)
        {
            sql.Append(" AND time >= $since");
            cmd.Parameters.AddWithValue("$since", since);
        }
        if (f.Until is { } until)
        {
            sql.Append(" AND time <= $until");
            cmd.Parameters.AddWithValue("$until", until);
        }
        if (f.Correlation is { } correlation)
        {
            sql.Append(" AND correlationid = $correlation");
            cmd.Parameters.AddWithValue("$correlation", correlation);
        }
        if (f.Thread is { } thread)
        {
            sql.Append(" AND (subject = $thread OR correlationid = $thread)");
            cmd.Parameters.AddWithValue("$thread", thread);
        }
        if (f.Readable is { } readable)
        {
            var any = readable.Select((g, i) => $"scope GLOB $read{i}").ToList();
            sql.Append(" AND (").Append(any.Count == 0 ? "0" : string.Join(" OR ", any)).Append(')');
            for (var i = 0; i < readable.Count; i++)
                cmd.Parameters.AddWithValue($"$read{i}", Glob.ToSqlite(readable[i]));
        }
        sql.Append(f.Newest ? " ORDER BY seq DESC" : " ORDER BY seq").Append(" LIMIT $limit;");
        cmd.Parameters.AddWithValue("$limit", f.Limit);
        return sql.ToString();
    }

    private static void Match(StringBuilder sql, SqliteCommand cmd, string column, string? glob)
    {
        if (glob is null)
            return;
        sql.Append(CultureInfo.InvariantCulture, $" AND {column} GLOB ${column}");
        cmd.Parameters.AddWithValue($"${column}", Glob.ToSqlite(glob));
    }

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
