using Chargehand.Contracts;

namespace Chargehand.Driven;

public enum TaskState { Completed, Failed, NeedsInput, NotStarted, Cancelled }

/// <summary>One task's end (ADR 0039). <c>RunId</c> is the run in the run store that holds its own <c>result/v1</c>.</summary>
public sealed record TaskOutcome(string Id, TaskState State, string? RunId = null, string? Detail = null, ErrorCode? Error = null, decimal Usd = 0, long Tokens = 0,
    string? Branch = null, string? PrUrl = null);

/// <summary>What a running task has used so far, cumulative.</summary>
public sealed record TaskUsage(long Tokens, decimal Usd);

/// <summary>The most one task may use. Tokens bind in both credential modes; <c>MaxUsd</c> is null where the credential is not priced.</summary>
public sealed record TaskLimits(long MaxTokens, decimal? MaxUsd);

public sealed record ResolvedTask(string Id, string? Ref, string Goal);

/// <summary>Runs one task to its end: the container, the session, the handover. It reports usage as it goes and stops when cancelled.</summary>
public interface ITaskRunner
{
    Task<TaskOutcome> RunAsync(ResolvedTask task, IProgress<TaskUsage> progress, CancellationToken ct);
}

/// <summary>Turns a tracker item id into a goal.</summary>
public interface ITaskSource
{
    /// <exception cref="TaskSourceException">The item cannot be read or has no title.</exception>
    Task<string> ResolveAsync(string @ref, CancellationToken ct);
}

public sealed class TaskSourceException(string message) : Exception(message);

/// <param name="MaxParallel">Tasks of this batch at once.</param>
/// <param name="MaxTokensTotal">Input plus output tokens over the batch; null for none.</param>
/// <param name="MaxUsdTotal">Dollars over the batch; null for none.</param>
/// <param name="Global">Shared between batches: a task holds one slot while it runs (driven.max_parallel_total).</param>
public sealed record BatchLimits(int MaxParallel, long? MaxTokensTotal, decimal? MaxUsdTotal, TaskLimits PerTask, SemaphoreSlim? Global = null);

/// <param name="Action">What the caller should do about a stop the batch could not fix by itself (a rate-limited subscription); null otherwise.</param>
public sealed record BatchOutcome(IReadOnlyList<TaskOutcome> Tasks, string? Action)
{
    public bool AllCompleted => Tasks.All(t => t.State == TaskState.Completed);

    public long Tokens => Tasks.Sum(t => t.Tokens);

    public decimal Usd => Tasks.Sum(t => t.Usd);

    /// <summary>Ids of the tasks that did not end in a pull request.</summary>
    public IReadOnlyList<string> Incomplete => [.. Tasks.Where(t => t.State != TaskState.Completed).Select(t => t.Id)];
}
