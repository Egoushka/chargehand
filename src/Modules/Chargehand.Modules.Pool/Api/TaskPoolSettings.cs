namespace Chargehand.Modules.Pool.Api;

/// <summary>The profile's <c>http.pool</c> (ADR 0099): the task pool's ceiling, its decisions valve and its file.</summary>
/// <param name="WorkerTokens">The tokens one worker may use before it hands its scope to a successor.</param>
/// <param name="MaxOpenDecisions">Open decisions waiting on the owner at or below which the governor may start a worker.</param>
/// <param name="Database">The SQLite file; null: <c>pool.db</c> beside the run log.</param>
/// <param name="MaxBatchBytes">The UTF-8 bytes of titles and bodies one claim may take; the rest stays queued for the next claim. A single larger item is still claimed alone.</param>
public sealed record TaskPoolSettings(long WorkerTokens = 150_000, int MaxOpenDecisions = 5, string? Database = null, long MaxBatchBytes = 64 * 1024)
{
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (WorkerTokens <= 0)
            problems.Add("worker_tokens must be a positive number of tokens");
        if (MaxOpenDecisions < 0)
            problems.Add("max_open_decisions must be zero or more");
        if (Database is { } db && string.IsNullOrWhiteSpace(db))
            problems.Add("database must name a file or be left out");
        if (MaxBatchBytes <= 0)
            problems.Add("max_batch_bytes must be a positive number of bytes");
        return problems;
    }
}
