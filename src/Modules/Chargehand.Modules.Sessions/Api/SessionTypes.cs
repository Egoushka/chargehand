using System.Text.Json.Serialization;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Sessions.Api;

/// <param name="Goal">What the change should do; it goes after <c>/chargehand:change</c>, so it cannot replace the skill.</param>
/// <param name="Branch">The branch the skill is told to use, <c>chargehand/&lt;run&gt;</c>.</param>
/// <param name="MaxTokens">Input plus output tokens the session may use; counted from the stream, so it binds with a subscription too.</param>
/// <param name="MaxUsd">Passed to <c>--max-budget-usd</c>; only meaningful where the credential is priced.</param>
/// <param name="Prices">The profile's price table, for the dollar-equivalent estimate in <c>session-usage.json</c>; left out of the file when null, so a profile without prices still
/// starts an older session image.</param>
/// <param name="Governor">ADR 0045's governor for this session; null or <c>off</c>: none.</param>
/// <param name="Environment">What the server knows for the session's environment note (ADR 0048); null: the note has the image's facts only.</param>
/// <param name="Acceptance">The task's acceptance criteria, run after the repository's own checks (<see cref="SessionChecks"/>); null: the repository's checks only.</param>
/// <param name="CheckRetries">How many times a failed check is sent back to the session as its next turn before the session ends as <c>checks_failed</c>.</param>
/// <param name="ReminderMinutes">The goal and criteria are re-sent after this many minutes (<see cref="GoalReminder"/>); null with <paramref name="ReminderTurns"/> null: never.</param>
/// <param name="ReminderTurns">The goal and criteria are re-sent after this many assistant messages; null: by minutes only.</param>
public sealed record SessionTask(string RunId, string Goal, string Branch, int MaxTurns, int MaxMinutes, int NoProgressSeconds,
    long? MaxTokens = null, string? Model = null, decimal? MaxUsd = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, ModelPrice>? Prices = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SessionGovernor? Governor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SessionEnvironment? Environment = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AcceptanceCheck>? Acceptance = null,
    int CheckRetries = SessionChecks.DefaultRetries,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ReminderMinutes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ReminderTurns = null);

/// <summary>How to start Claude Code: the program and arguments that go before its own (a test runs a script as <c>sh script</c>).</summary>
public sealed record SessionCommand(string File, IReadOnlyList<string> LeadingArgs)
{
    public static SessionCommand Claude { get; } = new("claude", []);
}

/// <param name="Secrets">Values replaced by <c>[redacted]</c> in everything the driver stores.</param>
/// <param name="PromptText">Appended to Claude Code's system prompt.</param>
/// <param name="McpUrl">The chargehand server's MCP address on the batch network, for the skill's research and review calls.</param>
/// <param name="McpToken">The run-scoped token for it.</param>
/// <param name="GitTimeout">How long one of the stop snapshot's git calls may run before it is killed and the snapshot skipped; null: 3 minutes.</param>
/// <param name="GitRunning">Whether some git process is alive (a lock file may then be live); null: look at the process table.</param>
public sealed record SessionOptions(string WorkDirectory, string OutDirectory, SessionTask Task, SessionCommand Command, IReadOnlyList<string> Secrets,
    string PromptText, string? McpUrl, string? McpToken, string PluginDirectory = "/opt/chargehand-plugin", TimeSpan? GitTimeout = null, Func<bool>? GitRunning = null);

/// <summary>The running tally the session writes to <c>session-usage.json</c>: input plus output tokens so far, assistant messages so far, and the <c>change</c> skill's step of the
/// latest tool call that names one (<see cref="Adherence.StageOf"/>). <c>CacheRead</c> and <c>CacheWrite</c> are summed over the assistant messages so far; <c>UsdEstimate</c> prices
/// all four token classes with the task's price table, cache reads at the cache-read rate. It is a dollar equivalent, not a bill, so it exists on a subscription too; null
/// when the task carries no prices or no message could be priced; if only some could, it sums those and <c>UsdEstimatePartial</c> is true. The real dollars (<c>total_cost_usd</c>) arrive only in the stream's <c>result</c>.</summary>
public sealed record SessionUsage(long Tokens, int? Turns = null, string? Stage = null, long? CacheRead = null, long? CacheWrite = null, decimal? UsdEstimate = null,
    bool UsdEstimatePartial = false);

/// <summary><c>RateLimited</c>: the session ended on a usage or rate limit, so the provider, not the task, is why it stopped.</summary>
public enum SessionStatus { Completed, NeedsInput, Stalled, TokenCap, Failed, RateLimited }

/// <param name="Reason">For a stall, its wire name; for a failure, a short cause.</param>
/// <param name="BundleWritten">A bundle of the session's branch is in the output directory; the handover decides whether to use it.</param>
/// <param name="StreamSha256">The SHA-256 of the stored stream (<c>stream.jsonl</c>, secrets redacted), so a result can cite the log without carrying it.</param>
/// <param name="SessionId">Claude Code's session id, which its own telemetry carries as <c>session.id</c>.</param>
/// <param name="Model">The model the session started on (the stream's <c>init</c>).</param>
/// <param name="ModelUsage">Tokens per model from the stream's <c>result</c>: a session can call more than one model.</param>
/// <param name="UsdEstimate">The session's dollar equivalent from its per-message tokens (see <see cref="SessionUsage"/>).</param>
/// <param name="RateLimitResetsAt">For <see cref="SessionStatus.RateLimited"/>: when the limit lifts, if the result said.</param>
/// <param name="Commits">Commits on the session's branch past the commit the workspace started at (the base), the seed's included; 0 with no bundle.</param>
/// <param name="SnapshotNote">What the stop snapshot did not do: files left out for their size, or why nothing was committed; null when there is nothing to say.</param>
/// <param name="Governor">The governor's decisions (ADR 0045), the first <see cref="SessionDriver.MaxGovernorDecisions"/>; null when no governor ran. <c>governor.jsonl</c> has them all.</param>
/// <param name="GovernorLogSha256">The SHA-256 of <c>governor.jsonl</c>; null when no governor ran.</param>
/// <param name="Checks">The last run of each repository check and acceptance criterion; null when none ran (the session did not finish, or there were none).</param>
/// <param name="GoalReminders">How many times the goal was queued for the session again (<see cref="GoalReminder"/>).</param>
public sealed record SessionOutcome(SessionStatus Status, string Reason, int Turns, long InputTokens, long OutputTokens, long CacheReadTokens,
    decimal? CostUsd, int? ExitCode, IReadOnlyList<string> Questions, bool BundleWritten, string? StreamSha256 = null, long CacheWriteTokens = 0, string? SessionId = null,
    string? Model = null, IReadOnlyDictionary<string, TokenCounts>? ModelUsage = null, decimal? UsdEstimate = null, DateTimeOffset? RateLimitResetsAt = null, bool UsdEstimatePartial = false,
    int Commits = 0, string? SnapshotNote = null, IReadOnlyList<GovernorDecision>? Governor = null, string? GovernorLogSha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CheckResult>? Checks = null, int GoalReminders = 0);
