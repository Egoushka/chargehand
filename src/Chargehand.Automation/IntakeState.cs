using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Automation;

public enum IntakePhase { Running, Asked, Done }

/// <summary>A task intake started for an issue, from the batch's start to the comment that says how it ended.</summary>
/// <param name="TaskId">The task's id in the batch.</param>
/// <param name="Size">The size class the task ran with; a continuation keeps it.</param>
/// <param name="AskedCommentId">The id of the comment that carries the session's questions; only a comment after it answers them.</param>
/// <param name="PrUrl">The pull request the task opened, once it did.</param>
/// <param name="Branch">The task's branch, once it pushed one; a review round's, from the start.</param>
/// <param name="Round">The number of the review round this task is (shared with the CI fix attempts of the pull request); null for a task started from an issue. <paramref name="Issue"/> is then the pull request's number.</param>
/// <param name="Trigger">The key of the trigger that started the task fresh from its issue; null for a continuation or a round. A run a restart lost is started again from it once (issue #557).</param>
public sealed record IntakeTask(string Repository, int Issue, string BatchRunId, string TaskId, DateTimeOffset Started, string Size, IntakePhase Phase = IntakePhase.Running,
    long? AskedCommentId = null, string? PrUrl = null, string? Branch = null, int? Round = null, string? Trigger = null);

/// <summary>A review of a pull request's head commit that is running, or finished and not yet commented (ADR 0051). The run is a plain read-only run, not a driven batch.</summary>
public sealed record PullReview(string Repository, int Pull, string Sha, string RunId, DateTimeOffset Started);

/// <summary>A comment on a chargehand pull request that waits for a review round (ADR 0051): read once, kept here until a round takes it or the pull request makes it moot.</summary>
/// <param name="Id"><c>r&lt;id&gt;</c> for an inline review comment, <c>c&lt;id&gt;</c> for a conversation comment: two id spaces, one list.</param>
/// <param name="Path">The file an inline comment is on; null for a conversation comment.</param>
public sealed record PendingReview(string Id, string Repository, int Pull, string Author, string Body, string? Path, int? Line, DateTimeOffset At);

/// <summary>A trigger that waits to start (CHARGEHAND-297): kept in the state file until it starts or is withdrawn, so a restart and the console see it. It is not in <see cref="IntakeState.Handled"/>.</summary>
/// <param name="Key">The trigger's key, as <see cref="IntakeState.Handled"/> will hold it once it starts.</param>
/// <param name="Issue">The issue, or the pull request of a review round.</param>
/// <param name="Since">When it first waited.</param>
/// <param name="Reason"><c>max_concurrent</c> (no free slot), <c>admission</c> (the server or <c>automation.account</c> refused the start, ADR 0076), <c>checkout</c> (the base commit could not be read)
/// or <c>lost</c> (a restart lost the issue's run, which starts again once; issue #557).</param>
/// <param name="Detail">The refusal as the server gave it, for <c>admission</c> and <c>checkout</c>.</param>
public sealed record WaitingTrigger(string Key, string Repository, int Issue, DateTimeOffset Since, string Reason, string? Detail = null);

/// <summary>What intake did with a trigger, and when: the console's answer to "why did nothing start" (CHARGEHAND-214).</summary>
/// <param name="Key">The trigger's key as <see cref="IntakeState.Handled"/> holds it (<c>label:owner/repo#12:&lt;event id&gt;</c>, <c>comment:owner/repo:&lt;comment id&gt;</c>).</param>
/// <param name="Outcome"><c>started</c>, <c>continued</c> (an answer), <c>round_started</c> (review comments), <c>queued</c> (a review comment waits for a round) or <c>ignored</c>.</param>
/// <param name="Reason">For <c>ignored</c>: <c>author_not_allowed</c>, <c>active</c> (already working on the issue), <c>author</c> (a merge repository takes issues of allowed authors only), <c>cap</c> (the daily cap of releases before 0.19.4),
/// <c>gone</c> (the earlier run is not in the log), <c>withdrawn</c> (a waiting trigger's label, comment or issue is gone), <c>not_chargehand_branch</c> or <c>review_rounds_off</c>.</param>
/// <param name="Author">The GitHub login that set the label or wrote the comment, where there is one.</param>
public sealed record IntakeTrigger(DateTimeOffset At, string Key, string Outcome, string? Reason = null, string? Author = null);

/// <summary>What intake remembers between polls and across a restart (ADR 0051), in one JSON file written whole through a temporary file. <see cref="Handled"/> holds every trigger that was acted on
/// or ignored, by key (<c>label:owner/repo#12:&lt;event id&gt;</c>, <c>comment:owner/repo:&lt;comment id&gt;</c>), and it only grows: a trigger seen once is never acted on again.</summary>
public sealed class IntakeState
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly string? _path;

    public IntakeState()
    {
    }

    private IntakeState(string path) => _path = path;

    /// <summary>Per repository, the update time of the newest comment read: the next poll asks for comments from then on.</summary>
    public Dictionary<string, DateTimeOffset> CommentCursor { get; init; } = [];

    /// <summary>Per labelled issue (<c>owner/repo#12</c>), its update time when its label events were last settled: an issue GitHub reports unchanged is not looked at again.</summary>
    public Dictionary<string, DateTimeOffset> LabelSeen { get; init; } = [];

    /// <summary>Per repository, the update time of the newest inline review comment read.</summary>
    public Dictionary<string, DateTimeOffset> ReviewCursor { get; init; } = [];

    public List<PendingReview> Pending { get; init; } = [];

    public HashSet<string> Handled { get; init; } = [];

    /// <summary>Comments intake wrote about a trigger (a wait, a refusal), by key, so each is written once.</summary>
    public HashSet<string> Notified { get; init; } = [];

    public List<IntakeTask> Tasks { get; init; } = [];

    /// <summary>Triggers whose batch began to start, by key, with the time: saved before the start, so after a crash the run log tells whether the batch exists (<see cref="TriggeredStart"/>). Removed with the trigger's settling.</summary>
    public Dictionary<string, DateTimeOffset> Starting { get; init; } = [];

    /// <summary>What intake did with the latest <see cref="MaxTriggers"/> triggers, oldest first. <see cref="Handled"/> says a trigger was seen; this says when and what came of it.</summary>
    public List<IntakeTrigger> Triggers { get; init; } = [];

    public const int MaxTriggers = 200;

    /// <summary>Triggers that wait for a slot or for admission, oldest first.</summary>
    public List<WaitingTrigger> Waiting { get; init; } = [];

    /// <summary>Head commits already given a review, as <c>owner/repo#12@sha</c>; it only grows, so a commit is reviewed once.</summary>
    public HashSet<string> Reviewed { get; init; } = [];

    /// <summary>Repositories whose trigger label intake has made sure of (created when it was missing), so each is checked once.</summary>
    public HashSet<string> Labeled { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reviews that run or wait for their comment.</summary>
    public List<PullReview> Reviews { get; init; } = [];


    public static IntakeState Load(string path)
    {
        if (!File.Exists(path))
            return new IntakeState(path);
        var state = JsonSerializer.Deserialize<IntakeState>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path}: empty intake state");
        return new IntakeState(path)
        {
            CommentCursor = state.CommentCursor ?? [],
            LabelSeen = state.LabelSeen ?? [],
            ReviewCursor = state.ReviewCursor ?? [],
            Pending = state.Pending ?? [],
            Handled = state.Handled ?? [],
            Notified = state.Notified ?? [],
            Tasks = state.Tasks ?? [],
            Starting = state.Starting ?? [],
            Waiting = state.Waiting ?? [],
            Triggers = state.Triggers ?? [],
            Reviewed = state.Reviewed ?? [],
            Reviews = state.Reviews ?? [],
            Labeled = new HashSet<string>(state.Labeled ?? [], StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>The latest task for an issue, if intake started one.</summary>
    public IntakeTask? LatestFor(string repository, int issue) => Tasks.LastOrDefault(t => t.Repository == repository && t.Issue == issue);

    public void Replace(IntakeTask old, IntakeTask updated) => Tasks[Tasks.IndexOf(old)] = updated;

    public void Save(DateTimeOffset now)
    {
        if (Triggers.Count > MaxTriggers)
            Triggers.RemoveRange(0, Triggers.Count - MaxTriggers);
        if (_path is null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temp = $"{_path}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, _path, overwrite: true);
    }
}
