using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Automation;

public enum IntakePhase { Running, Asked, Done }

/// <summary>A task intake started for an issue, from the batch's start to the comment that says how it ended.</summary>
/// <param name="TaskId">The task's id in the batch.</param>
/// <param name="Size">The size class the task ran with; a continuation keeps it.</param>
/// <param name="AskedCommentId">The id of the comment that carries the session's questions; only a comment after it answers them.</param>
/// <param name="PrUrl">The pull request the task opened, once it did.</param>
/// <param name="Branch">The task's branch, once it pushed one.</param>
public sealed record IntakeTask(string Repository, int Issue, string BatchRunId, string TaskId, DateTimeOffset Started, string Size, IntakePhase Phase = IntakePhase.Running,
    long? AskedCommentId = null, string? PrUrl = null, string? Branch = null);

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

    public HashSet<string> Handled { get; init; } = [];

    /// <summary>Comments intake wrote about a trigger (a wait, a refusal), by key, so each is written once.</summary>
    public HashSet<string> Notified { get; init; } = [];

    public List<IntakeTask> Tasks { get; init; } = [];

    /// <summary>When triggered batches started, for the daily cap; entries older than a day are dropped when the state is saved.</summary>
    public List<DateTimeOffset> Starts { get; init; } = [];

    public static IntakeState Load(string path)
    {
        if (!File.Exists(path))
            return new IntakeState(path);
        var state = JsonSerializer.Deserialize<IntakeState>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path}: empty intake state");
        return new IntakeState(path)
        {
            CommentCursor = state.CommentCursor ?? [],
            LabelSeen = state.LabelSeen ?? [],
            Handled = state.Handled ?? [],
            Notified = state.Notified ?? [],
            Tasks = state.Tasks ?? [],
            Starts = state.Starts ?? [],
        };
    }

    /// <summary>The latest task for an issue, if intake started one.</summary>
    public IntakeTask? LatestFor(string repository, int issue) => Tasks.LastOrDefault(t => t.Repository == repository && t.Issue == issue);

    public void Replace(IntakeTask old, IntakeTask updated) => Tasks[Tasks.IndexOf(old)] = updated;

    public void Save(DateTimeOffset now)
    {
        Starts.RemoveAll(s => s <= now - TimeSpan.FromDays(1));
        if (_path is null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temp = $"{_path}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, _path, overwrite: true);
    }
}
