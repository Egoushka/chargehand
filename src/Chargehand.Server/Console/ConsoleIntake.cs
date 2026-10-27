using Chargehand.Automation;
using Chargehand.Config;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Server;

public sealed record IntakeRepositoryBody(string Repository, string Path);

public sealed record IntakeCaps(int MaxConcurrent, int MaxReviewRounds, long MaxTokensTotal, decimal MaxUsdTotal, string BudgetClass);

/// <summary>A trigger that waits to start, oldest first (CHARGEHAND-297): <paramref name="Reason"/> is <c>max_concurrent</c>, <c>review_backlog</c>, <c>subscription</c>, <c>breaker</c>, <c>admission</c> or <c>checkout</c>, <paramref name="Detail"/> the server's refusal.</summary>
public sealed record IntakeWaitingBody(string Trigger, string Repository, int Issue, DateTimeOffset Since, string Reason, string? Detail);

/// <summary>The subscription check of the intake governor (ADR 0084): <paramref name="Block"/> is <c>automation.github.subscription_block</c>; <paramref name="ResetsAt"/> is when the rate-limit pause a running task reported ends, null when none is active.</summary>
public sealed record IntakeSubscriptionBody(bool Block, DateTimeOffset? ResetsAt);

public sealed record IntakeTaskBody(string Repository, int Issue, string BatchRunId, string TaskId, DateTimeOffset Started, string Size, string Phase, string? PrUrl, string? Branch, int? Round, bool AwaitingAnswer);

/// <summary>A review comment waiting for a round. The comment's text is not returned.</summary>
public sealed record IntakePendingBody(string Id, string Repository, int Pull, string Author, string? Path, int? Line, DateTimeOffset At);

/// <param name="Reason">Why intake did not act, as it wrote it on the issue: <c>active</c> (already working on it), <c>author</c> (a merge repository needs an issue by an allowed author), <c>gone</c> (the run is no longer
/// in the log), <c>wait</c> (no free slot), <c>cap</c> (the daily cap of releases before 0.19.4), <c>notask</c>, <c>busy</c>, <c>rounds</c> (review rounds used up).</param>
public sealed record IntakeNoticeBody(string Reason, string Trigger);

/// <summary>What intake did with a trigger, newest first (CHARGEHAND-214). <paramref name="Outcome"/> is <c>started</c>, <c>continued</c>, <c>round_started</c>, <c>queued</c> or <c>ignored</c>; for <c>ignored</c> the
/// <paramref name="Reason"/> is <c>author_not_allowed</c>, <c>active</c>, <c>author</c>, <c>cap</c> (before 0.19.4), <c>gone</c>, <c>withdrawn</c>, <c>not_chargehand_branch</c> or <c>review_rounds_off</c>. <paramref name="Author"/> is the login that set the label or wrote the comment.</summary>
public sealed record IntakeTriggerBody(DateTimeOffset At, string Trigger, string Outcome, string? Reason, string? Author);

/// <param name="HandledLabels">Label triggers seen, acted on or ignored; a trigger is handled once.</param>
public sealed record IntakeHandledBody(int Total, int Labels, int Comments, int Reviews, int Rounds);

/// <summary><c>intake/v1</c>: GitHub intake's settings and what it remembers (<c>intake/github.json</c>), read from the file the poller saves. <c>Enabled</c> is false when the profile has no <c>automation.github</c>.</summary>
public sealed record IntakeBody(string ContractVersion, DateTimeOffset GeneratedAt, bool Enabled, bool StatePresent, DateTimeOffset? StateSavedAt, IReadOnlyList<IntakeRepositoryBody> Watched,
    IReadOnlyList<string> AllowedAuthors, string? Label, string? Mention, int? PollSeconds, IntakeCaps? Caps, int Running, IReadOnlyList<IntakeTaskBody> Tasks,
    IReadOnlyList<IntakePendingBody> PendingReviews, IntakeHandledBody Handled, IReadOnlyList<IntakeNoticeBody> Notices, IReadOnlyDictionary<string, DateTimeOffset> CommentCursors,
    IReadOnlyList<string> NotRecorded, IReadOnlyList<IntakeTriggerBody>? Triggers = null, IReadOnlyList<IntakeWaitingBody>? Waiting = null, IntakeSubscriptionBody? Subscription = null);

internal static class ConsoleIntake
{
    public const string Contract = "intake/v1";

    private const int MaxTasks = 100;
    private const int MaxNotices = 200;
    private const int MaxTriggers = 100;

    private static readonly IReadOnlyList<string> NotRecorded =
    [
        "Triggers are logged from the release that added the log, the latest 200: a trigger handled before has only its key in the handled counts, and a review comment that was not a request (anyone's comment on a pull request that is not chargehand's) is not logged.",
        "Handled keys and notices are not timestamped, so they are not ordered; the triggers log is.",
        "The state file is the poller's last save: a change since then is not visible, and the read does not wait for a poll.",
    ];

    public static IntakeBody Build(ConsoleContext c)
    {
        var settings = c.Profile?.Automation?.Github;
        var path = c.Settings.IntakeStatePath;
        var info = path is not null && File.Exists(path) ? new FileInfo(path) : null;
        if (settings is null)
            return new IntakeBody(Contract, c.Now, false, info is not null, null, [], [], null, null, null, null, 0, [], [], new(0, 0, 0, 0, 0), [], new Dictionary<string, DateTimeOffset>(), NotRecorded);
        var state = new IntakeState();
        string? problem = null;
        if (info is not null)
            try
            {
                state = IntakeState.Load(path!);
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException or IOException)
            {
                problem = $"the state file could not be read: {e.Message}";
            }
        var tasks = state.Tasks.OrderByDescending(t => t.Started).Take(MaxTasks)
            .Select(t => new IntakeTaskBody(t.Repository, t.Issue, t.BatchRunId, t.TaskId, t.Started, t.Size, t.Phase.ToString().ToLowerInvariant(), t.PrUrl, t.Branch, t.Round, t.Phase == IntakePhase.Asked)).ToList();
        return new IntakeBody(Contract, c.Now, true, info is not null, info?.LastWriteTimeUtc, [.. (settings.Repositories ?? new Dictionary<string, AutomationRepository>()).OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => new IntakeRepositoryBody(r.Key, r.Value.Path))],
            [.. settings.AllowedAuthors ?? []], settings.Label, settings.Mention, settings.PollSeconds,
            new IntakeCaps(settings.MaxConcurrent, settings.MaxReviewRounds, settings.MaxTokensTotal, settings.MaxUsdTotal, settings.BudgetClass),
            state.Tasks.Count(t => t.Phase == IntakePhase.Running), tasks,
            [.. state.Pending.OrderByDescending(p => p.At).Select(p => new IntakePendingBody(p.Id, p.Repository, p.Pull, p.Author, p.Path, p.Line, p.At))],
            new IntakeHandledBody(state.Handled.Count, state.Handled.Count(k => k.StartsWith("label:", StringComparison.Ordinal)), state.Handled.Count(k => k.StartsWith("comment:", StringComparison.Ordinal)),
                state.Handled.Count(k => k.StartsWith("review:", StringComparison.Ordinal)), state.Handled.Count(k => k.StartsWith("round:", StringComparison.Ordinal))),
            [.. state.Notified.Select(Notice).OrderBy(n => n.Reason, StringComparer.Ordinal).ThenBy(n => n.Trigger, StringComparer.Ordinal).Take(MaxNotices)],
            state.CommentCursor, problem is null ? NotRecorded : [problem, .. NotRecorded],
            [.. state.Triggers.OrderByDescending(t => t.At).Take(MaxTriggers).Select(t => new IntakeTriggerBody(t.At, t.Key, t.Outcome, t.Reason, t.Author))],
            [.. state.Waiting.OrderBy(w => w.Since).Select(w => new IntakeWaitingBody(w.Key, w.Repository, w.Issue, w.Since, w.Reason, w.Detail))],
            new IntakeSubscriptionBody(settings.SubscriptionBlock, c.Driven?.RateLimitPauses(c.Now) is { Count: > 0 } pauses ? pauses.Max(p => p.ResetsAt) : null));
    }

    /// <summary>A notified key is <c>&lt;reason&gt;:&lt;trigger&gt;</c>.</summary>
    private static IntakeNoticeBody Notice(string key) => key.IndexOf(':') is > 0 and var colon ? new(key[..colon], key[(colon + 1)..]) : new("other", key);
}
