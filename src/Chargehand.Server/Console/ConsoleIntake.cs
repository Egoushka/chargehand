using Chargehand.Automation;
using Chargehand.Config;

namespace Chargehand.Server;

public sealed record IntakeRepositoryBody(string Repository, string Path);

public sealed record IntakeCaps(int MaxConcurrent, int MaxStartsPerDay, int MaxReviewRounds, long MaxTokensTotal, decimal MaxUsdTotal, string BudgetClass);

public sealed record IntakeStarts(int Last24h, int Cap, int Remaining, DateTimeOffset? NextSlotAt);

public sealed record IntakeTaskBody(string Repository, int Issue, string BatchRunId, string TaskId, DateTimeOffset Started, string Size, string Phase, string? PrUrl, string? Branch, int? Round, bool AwaitingAnswer);

/// <summary>A review comment waiting for a round. The comment's text is not returned.</summary>
public sealed record IntakePendingBody(string Id, string Repository, int Pull, string Author, string? Path, int? Line, DateTimeOffset At);

/// <param name="Reason">Why intake did not act, as it wrote it on the issue: <c>active</c> (already working on it), <c>author</c> (a merge repository needs an issue by an allowed author), <c>gone</c> (the run is no longer
/// in the log), <c>wait</c> (no free slot), <c>cap</c> (the daily cap), <c>notask</c>, <c>busy</c>, <c>rounds</c> (review rounds used up).</param>
public sealed record IntakeNoticeBody(string Reason, string Trigger);

/// <param name="HandledLabels">Label triggers seen, acted on or ignored; a trigger is handled once.</param>
public sealed record IntakeHandledBody(int Total, int Labels, int Comments, int Reviews, int Rounds);

/// <summary><c>intake/v1</c>: GitHub intake's settings and what it remembers (<c>intake/github.json</c>), read from the file the poller saves. <c>Enabled</c> is false when the profile has no <c>automation.github</c>.</summary>
public sealed record IntakeBody(string ContractVersion, DateTimeOffset GeneratedAt, bool Enabled, bool StatePresent, DateTimeOffset? StateSavedAt, IReadOnlyList<IntakeRepositoryBody> Watched,
    IReadOnlyList<string> AllowedAuthors, string? Label, string? Mention, int? PollSeconds, IntakeCaps? Caps, IntakeStarts? Starts, int Running, IReadOnlyList<IntakeTaskBody> Tasks,
    IReadOnlyList<IntakePendingBody> PendingReviews, IntakeHandledBody Handled, IReadOnlyList<IntakeNoticeBody> Notices, IReadOnlyDictionary<string, DateTimeOffset> CommentCursors,
    IReadOnlyList<string> NotRecorded);

internal static class ConsoleIntake
{
    public const string Contract = "intake/v1";

    private const int MaxTasks = 100;
    private const int MaxNotices = 200;

    private static readonly IReadOnlyList<string> NotRecorded =
    [
        "A trigger from an author who is not allowed is marked handled and ignored without a reason; only the cases intake comments on (see notices) carry one.",
        "Handled keys and notices are not timestamped, so they are not ordered.",
        "The state file is the poller's last save: a change since then is not visible, and the read does not wait for a poll.",
    ];

    public static IntakeBody Build(ConsoleContext c)
    {
        var settings = c.Profile?.Automation?.Github;
        var path = c.Settings.IntakeStatePath;
        var info = path is not null && File.Exists(path) ? new FileInfo(path) : null;
        if (settings is null)
            return new IntakeBody(Contract, c.Now, false, info is not null, null, [], [], null, null, null, null, null, 0, [], [], new(0, 0, 0, 0, 0), [], new Dictionary<string, DateTimeOffset>(), NotRecorded);
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
        var day = c.Now - TimeSpan.FromDays(1);
        var starts = state.Starts.Where(s => s > day).OrderBy(s => s).ToList();
        var full = starts.Count >= settings.MaxStartsPerDay;
        var tasks = state.Tasks.OrderByDescending(t => t.Started).Take(MaxTasks)
            .Select(t => new IntakeTaskBody(t.Repository, t.Issue, t.BatchRunId, t.TaskId, t.Started, t.Size, t.Phase.ToString().ToLowerInvariant(), t.PrUrl, t.Branch, t.Round, t.Phase == IntakePhase.Asked)).ToList();
        return new IntakeBody(Contract, c.Now, true, info is not null, info?.LastWriteTimeUtc, [.. (settings.Repositories ?? new Dictionary<string, AutomationRepository>()).OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => new IntakeRepositoryBody(r.Key, r.Value.Path))],
            [.. settings.AllowedAuthors ?? []], settings.Label, settings.Mention, settings.PollSeconds,
            new IntakeCaps(settings.MaxConcurrent, settings.MaxStartsPerDay, settings.MaxReviewRounds, settings.MaxTokensTotal, settings.MaxUsdTotal, settings.BudgetClass),
            new IntakeStarts(starts.Count, settings.MaxStartsPerDay, Math.Max(0, settings.MaxStartsPerDay - starts.Count), full ? starts[starts.Count - settings.MaxStartsPerDay] + TimeSpan.FromDays(1) : null),
            state.Tasks.Count(t => t.Phase == IntakePhase.Running), tasks,
            [.. state.Pending.OrderByDescending(p => p.At).Select(p => new IntakePendingBody(p.Id, p.Repository, p.Pull, p.Author, p.Path, p.Line, p.At))],
            new IntakeHandledBody(state.Handled.Count, state.Handled.Count(k => k.StartsWith("label:", StringComparison.Ordinal)), state.Handled.Count(k => k.StartsWith("comment:", StringComparison.Ordinal)),
                state.Handled.Count(k => k.StartsWith("review:", StringComparison.Ordinal)), state.Handled.Count(k => k.StartsWith("round:", StringComparison.Ordinal))),
            [.. state.Notified.Select(Notice).OrderBy(n => n.Reason, StringComparer.Ordinal).ThenBy(n => n.Trigger, StringComparer.Ordinal).Take(MaxNotices)],
            state.CommentCursor, problem is null ? NotRecorded : [problem, .. NotRecorded]);
    }

    /// <summary>A notified key is <c>&lt;reason&gt;:&lt;trigger&gt;</c>.</summary>
    private static IntakeNoticeBody Notice(string key) => key.IndexOf(':') is > 0 and var colon ? new(key[..colon], key[(colon + 1)..]) : new("other", key);
}
