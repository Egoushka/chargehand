using Chargehand.Contracts;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Modules.Tasks.Api;

public enum TaskState { Completed, Failed, NeedsInput, NotStarted, Cancelled }

/// <summary>One task's end (ADR 0039). <c>RunId</c> is the run in the run store that holds its own <c>result/v1</c>.</summary>
/// <param name="RetryAfter">For a <see cref="ErrorCode.RateLimited"/> end: when the limit lifts, if known; the scheduler holds new starts until then.</param>
/// <param name="Runner">The runner the task's sessions ran on, when the profile names its runners (ADR 0058).</param>
/// <param name="Replace">The task did not start because its runner could not be reached (ADR 0058): the scheduler places it again on another, a limited number of times, and never records this outcome.</param>
public sealed record TaskOutcome(string Id, TaskState State, string? RunId = null, string? Detail = null, ErrorCode? Error = null, decimal Usd = 0, long Tokens = 0,
    string? Branch = null, string? PrUrl = null, DateTimeOffset? RetryAfter = null, string? Runner = null, bool Replace = false);

/// <summary>The most one task may use. Tokens bind in both credential modes; <c>MaxUsd</c> is null where the credential is not priced.</summary>
public sealed record TaskLimits(long MaxTokens, decimal? MaxUsd);

/// <param name="MaxTurns">The request's per-task override of the preset's turn limit; null: the preset's.</param>
/// <param name="MaxMinutes">The same for the session's wall clock.</param>
/// <param name="Size">The request's size class for the governor's budget; null: M.</param>
/// <param name="BudgetUsd">The request's own budget for the task, in place of the size class's.</param>
/// <param name="From">Set when the task continues an earlier batch's task (ADR 0045); <paramref name="Goal"/> then already carries the earlier goal, its questions and the answers.</param>
/// <param name="DependsOn">Ids of tasks of the batch that must end <c>completed</c> first (ADR 0049).</param>
/// <param name="Upstream">The branches of the finished tasks in <paramref name="DependsOn"/>, in that order; the scheduler fills it when the task starts.</param>
/// <param name="SplitFrom">Set when the planning step made this task out of a larger one (ADR 0049): the id of that task, which the chain's last link keeps.</param>
/// <param name="SizeFrom"><c>plan</c> when <paramref name="Size"/> is the planning step's estimate; null: the caller's (or no size at all). The learning record says which.</param>
/// <param name="Runner">The runner the task runs on (ADR 0058): set from the batch state for a task a restart took up, so it stays where its session is, and by the scheduler when it places a new one.</param>
/// <param name="Title">The request's title for the task's pull request; null: <see cref="PullRequestTitle.FromGoal"/> makes one.</param>
/// <param name="Acceptance">The request's acceptance criteria; the session's branch must pass them (and the repository's checks) to complete.</param>
public sealed record ResolvedTask(string Id, string? Ref, string Goal, int? MaxTurns = null, int? MaxMinutes = null, string? Size = null, decimal? BudgetUsd = null, ContinuedFrom? From = null,
    IReadOnlyList<string>? DependsOn = null, IReadOnlyList<UpstreamWork>? Upstream = null, string? SplitFrom = null, string? SizeFrom = null, string? Runner = null, string? Title = null,
    IReadOnlyList<AcceptanceCheck>? Acceptance = null);

/// <summary>A finished task a later one starts from (ADR 0049): its last session, whose output volume holds the bundle of its branch, and the branch and pull request it ended in.</summary>
/// <param name="Runner">The runner whose engine holds that session's output volume (ADR 0058); null: the only one.</param>
public sealed record UpstreamWork(string Id, string RunId, string Branch, string? PrUrl, string? Runner = null);

/// <summary>The earlier task a task continues: its last session (whose output volume may hold a handoff) and the work it kept; null: it made no commits, so the new task starts from the base commit.
/// <paramref name="Pushed"/> is set instead when the earlier task finished and its branch is on the remote with a pull request open (a review round, ADR 0051): the new task starts from that branch as the remote has it.</summary>
/// <param name="Runner">The runner whose engine holds the output volumes of the earlier task's sessions (ADR 0058); null: the only one.</param>
public sealed record ContinuedFrom(string LastRun, KeptWork? Kept, PushedWork? Pushed = null, string? Runner = null);

/// <summary>A finished task's branch, as its <c>branch</c> artifact records it: <paramref name="Base"/> is the commit the task started at, which a continuation has to start at too.</summary>
public sealed record PushedWork(string Branch, string Base);

/// <summary>What an unfinished task leaves for a later one, as the <c>kept-work</c> artifact of its result (ADR 0045). The bundle is in the output volume of <paramref name="BundleRun"/>.</summary>
/// <param name="Branch">The task branch, <c>chargehand/&lt;first run id&gt;</c>; a continuation uses the same name, because the bundle holds that ref.</param>
/// <param name="Commits">Commits past the base the bundle holds.</param>
/// <param name="Base">The commit the earlier task started at; a continuation has to start at the same one.</param>
public sealed record KeptWork(string Branch, string BundleRun, int Commits, string Base)
{
    /// <summary>The kind of the result artifact that carries it.</summary>
    public const string ArtifactKind = "kept-work";
}

/// <summary>Runs one task to its end: the container, the session, the handover. It reports usage as it goes and stops when cancelled.</summary>
public interface ITaskRunner
{
    Task<TaskOutcome> RunAsync(ResolvedTask task, IProgress<TaskUsage> progress, CancellationToken ct);
}

/// <param name="MaxParallel">Tasks of this batch at once.</param>
/// <param name="MaxTokensTotal">Input plus output tokens over the batch; null for none.</param>
/// <param name="MaxUsdTotal">Dollars over the batch; null for none.</param>
/// <param name="Global">Shared between batches: a task holds one slot while it runs (driven.max_parallel_total).</param>
public sealed record BatchLimits(int MaxParallel, long? MaxTokensTotal, decimal? MaxUsdTotal, TaskLimits PerTask, SemaphoreSlim? Global = null);

/// <param name="Action">What the caller should do about a stop the batch could not fix by itself (a rate-limited subscription); null otherwise.</param>
/// <param name="Error">Set when the batch stopped starting tasks because of the provider's usage or rate limit (<see cref="ErrorCode.RateLimited"/>): the batch is retryable after the reset.</param>
public sealed record BatchOutcome(IReadOnlyList<TaskOutcome> Tasks, string? Action, ErrorCode? Error = null)
{
    public bool AllCompleted => Tasks.All(t => t.State == TaskState.Completed);

    public long Tokens => Tasks.Sum(t => t.Tokens);

    public decimal Usd => Tasks.Sum(t => t.Usd);

    /// <summary>Ids of the tasks that did not end in a pull request.</summary>
    public IReadOnlyList<string> Incomplete => [.. Tasks.Where(t => t.State != TaskState.Completed).Select(t => t.Id)];
}
