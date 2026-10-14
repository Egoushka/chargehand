using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Tasks.Api;

/// <summary>One session of a task, written when it ends: the first, a continuation after a stop (ADR 0044), or a CI fix attempt (ADR 0046).</summary>
/// <param name="Role"><c>first</c>, <c>continuation</c> or <c>ci_fix</c>.</param>
/// <param name="Runner">The runner the session ran on when the profile names its runners (ADR 0058).</param>
/// <param name="Status">The session's status as its outcome file says it, snake case (<c>completed</c>, <c>stalled</c>, <c>token_cap</c>, <c>rate_limited</c>, ...); <c>cancelled</c> or <c>error</c> for one cut short before an outcome was read.</param>
/// <param name="Reason">Why it ended: a stall's wire name (<c>turn_limit</c>, <c>checkpoint_spend</c>, ...) or a short cause; null when the session finished.</param>
/// <param name="Usd">Priced dollars; null where the credential has no price (a subscription).</param>
/// <param name="UsdEstimate">The dollar equivalent of the session's tokens.</param>
public sealed record SessionRecord(string TaskId, string RunId, string Role, string? Runner, DateTimeOffset Started, DateTimeOffset Ended, string Status, string? Reason, int Turns, long InputTokens,
    long OutputTokens, long CacheReadTokens, long CacheWriteTokens, decimal? UsdEstimate, decimal? Usd, int? ExitCode, int Commits);

/// <summary>Every governor decision of one session, from its <c>governor.jsonl</c> (ADR 0045); the outcome file carries only the first 50. Values are cut to 200 characters and redacted by the session.</summary>
/// <param name="Total">How many decisions the log held; <paramref name="Decisions"/> is cut at <see cref="MaxDecisions"/>.</param>
public sealed record GovernorRecord(string TaskId, string RunId, int Total, IReadOnlyList<GovernorDecision> Decisions)
{
    public const int MaxDecisions = 200;
}

/// <summary>A required or optional check of a pull request as the integrator read it from the forge.</summary>
public sealed record CheckRecord(string Name, bool Required, string Outcome, long? JobId);

/// <summary>The pull request's checks at one look of the integrator's watch (ADR 0046), written when they change.</summary>
/// <param name="Decision"><c>wait</c>, <c>done</c>, <c>fix</c> or <c>give_up</c>: what the CI fixer's policy made of this look; <c>merged</c> or <c>closed</c> for the look that ended the watch.</param>
public sealed record ChecksRecord(string TaskId, DateTimeOffset At, int Pull, string HeadSha, string MergeStatus, bool Draft, bool AutoMerge, string Decision, IReadOnlyList<CheckRecord> Checks);

/// <summary>One CI fix attempt: started when the marker went up on the pull request, ended with the session's result.</summary>
public sealed record CiAttemptRecord(string TaskId, int Attempt, int MaxAttempts, DateTimeOffset At, string JobsKey, IReadOnlyList<string> Failing, bool Pushed, string Detail);
