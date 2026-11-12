namespace Chargehand.Modules.Pool.Api;

/// <summary>The profile's <c>http.pool</c> (ADR 0099): the task pool's ceiling, its decisions valve and its file.</summary>
/// <param name="WorkerTokens">The tokens one worker may use before it hands its scope to a successor.</param>
/// <param name="MaxOpenDecisions">Open decisions waiting on the owner at or below which the governor may start a worker.</param>
/// <param name="Database">The SQLite file; null: <c>pool.db</c> beside the run log.</param>
public sealed record PoolSettings(long WorkerTokens = 150_000, int MaxOpenDecisions = 5, string? Database = null)
{
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (WorkerTokens <= 0)
            problems.Add("http.pool.worker_tokens: must be a positive number of tokens");
        if (MaxOpenDecisions < 0)
            problems.Add("http.pool.max_open_decisions: must be zero or more");
        if (Database is { } db && string.IsNullOrWhiteSpace(db))
            problems.Add("http.pool.database: name a file or leave it out");
        return problems;
    }
}
