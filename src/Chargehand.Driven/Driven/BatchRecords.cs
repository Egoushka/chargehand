using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chargehand.Contracts;
using Chargehand.Modules.Journal.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Driven;

/// <summary>What <see cref="BatchRecords.ReadTasks"/> read for one batch.</summary>
public sealed record BatchTaskRecords(IReadOnlyList<SessionRecord> Sessions, IReadOnlyList<GovernorRecord> Governor, IReadOnlyList<ChecksRecord> Checks, IReadOnlyList<CiAttemptRecord> Attempts)
{
    public static BatchTaskRecords Empty { get; } = new([], [], [], []);
}

/// <summary>What the operator console needs and the run log does not keep (CHARGEHAND-214), one directory per driven batch beside the run log: <c>events.jsonl</c> (the batch's events, so a finished
/// batch's timeline outlives a restart) and <c>tasks.jsonl</c> (a line per session, governor log, check snapshot and CI fix attempt, tagged by <c>type</c>). Both are append-only and written by the
/// server process only. Nothing here is a secret or a prompt: events carry the text the live stream carries, and the rest is counts, names, times and the governor's own log. Size is bounded
/// three ways: a batch's events are capped (<see cref="MaxEvents"/>, and a task's <c>session_progress</c> is kept at most once in <see cref="ProgressEvery"/>) as are its task records, and the directories of
/// the oldest batches are deleted past <see cref="MaxBatches"/> or <see cref="MaxAge"/>, never those of a batch that is still running.</summary>
public sealed class BatchRecords(string directory, TimeProvider? clock = null)
{
    public const string EventsFile = "events.jsonl";
    public const string TasksFile = "tasks.jsonl";

    /// <summary>Events of one batch kept on disk; once it is reached only a task's end and the batch's end are still written.</summary>
    public const int MaxEvents = 5000;

    /// <summary>Lines of <c>tasks.jsonl</c> of one batch.</summary>
    public const int MaxTaskLines = 3000;

    public const int MaxDecisionsPerSession = GovernorRecord.MaxDecisions;

    /// <summary>Check snapshots and CI fix attempts kept per task.</summary>
    public const int MaxChecksPerTask = 60;

    public const int MaxDetail = 1500;

    public static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(30);

    public const int MaxBatches = 500;

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(180);

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _eventCounts = [];
    private readonly Dictionary<string, int> _taskLineCounts = [];
    private readonly Dictionary<(string Batch, string Task), DateTimeOffset> _lastProgress = [];
    private readonly Dictionary<(string Batch, string Type, string Task), int> _perTask = [];
    private readonly Dictionary<string, DrivenJournal> _journals = [];

    private TimeProvider Time => clock ?? TimeProvider.System;

    public string Root => directory;

    private string Dir(string batch) => Path.Combine(directory, batch);

    /// <summary>Appends an event of the batch. The result of a finished batch is left out (the run log has it), a detail is cut, and a task's progress is kept at most once in <see cref="ProgressEvery"/>.
    /// A batch given to <see cref="Journal"/> has each kept event appended to the journal too, until its <c>run_finished</c>.</summary>
    public void AppendEvent(string batch, RunStatus e)
    {
        if (!IsName(batch))
            return;
        var kept = e with { Result = null, Detail = Cut(e.Detail, MaxDetail) };
        lock (_gate)
        {
            if (e.Event == RunEventKind.SessionProgress && e.Tokens is not null && e.TaskId is { } task)
            {
                var now = Time.GetUtcNow();
                if (_lastProgress.TryGetValue((batch, task), out var last) && now - last < ProgressEvery)
                    return;
                _lastProgress[(batch, task)] = now;
            }
            // The batch's flow is forgotten before its run_finished (DrivenRun), so nothing after it goes to the journal.
            var journal = _journals.GetValueOrDefault(batch);
            if (e.Event == RunEventKind.RunFinished && _journals.Remove(batch))
                journal = null;
            var count = CountOf(_eventCounts, batch, EventsFile);
            var closing = e.Event is RunEventKind.TaskFinished or RunEventKind.RunFinished;
            if (count >= MaxEvents + (closing ? 500 : 0))
                return;
            Write(batch, EventsFile, JsonSerializer.Serialize(kept, ContractJson.Options));
            _eventCounts[batch] = count + 1;
            if (journal is not null)
                AppendToJournal(journal, batch, kept);
        }
    }

    /// <summary>From now on the batch's events go to <paramref name="journal"/> as well (ADR 0061), so a client that reconnects, even to the next process, replays what it missed.</summary>
    public void Journal(string batch, DrivenJournal journal)
    {
        lock (_gate)
            _journals[batch] = journal;
    }

    /// <summary>The kind an event is journaled under: its <see cref="RunEventKind"/> in snake case, as the event stream names it.</summary>
    public static string KindOf(RunStatus e) => JsonNamingPolicy.SnakeCaseLower.ConvertName((e.Event ?? RunEventKind.Started).ToString());

    /// <summary>Journaled events back as run statuses with their sequence numbers; one that does not parse is left out.</summary>
    public static IReadOnlyList<(long Sequence, RunStatus Event)> Parse(IEnumerable<BatchEvent> events)
    {
        List<(long, RunStatus)> parsed = [];
        foreach (var e in events)
        {
            try
            {
                if (JsonSerializer.Deserialize(e.Payload, ContractJsonContext.Default.RunStatus) is { } status)
                    parsed.Add((e.Sequence, status));
            }
            catch (JsonException)
            {
            }
        }
        return parsed;
    }

    private static void AppendToJournal(DrivenJournal journal, string batch, RunStatus e)
    {
        try
        {
            // Waited for under the lock, so the journal holds the events in the order they were published.
            journal.AppendEventAsync(batch, KindOf(e), JsonSerializer.Serialize(e, ContractJsonContext.Default.RunStatus)).GetAwaiter().GetResult();
        }
        catch (Exception x) when (x is DbException or InvalidOperationException or ObjectDisposedException)
        {
            Console.Error.WriteLine($"chargehand: could not journal an event of batch {batch}: {x.Message}");
        }
    }

    /// <summary>The events written for a batch, oldest first; null when there is no file (a batch from before the records, or one whose records were pruned).</summary>
    public IReadOnlyList<RunStatus>? ReadEvents(string batch)
    {
        if (!IsName(batch))
            return null;
        var path = Path.Combine(Dir(batch), EventsFile);
        lock (_gate)
        {
            if (!File.Exists(path))
                return null;
            List<RunStatus> events = [];
            foreach (var line in Lines(path))
            {
                try
                {
                    if (JsonSerializer.Deserialize<RunStatus>(line, ContractJson.Options) is { } status)
                        events.Add(status);
                }
                catch (JsonException)
                {
                }
            }
            return events;
        }
    }

    /// <summary>A session's line, once per run id: a task taken up after its session ended reads that session's outcome again (ADR 0043), and the line already written stands.</summary>
    public void Append(string batch, SessionRecord record) => AppendTask(batch, "session", record.TaskId, JsonSerializer.SerializeToNode(record, ContractJson.Options)!, run: record.RunId);

    /// <summary>A session's governor log, once per run id (see the session's line).</summary>
    public void Append(string batch, GovernorRecord record) => AppendTask(batch, "governor", record.TaskId, JsonSerializer.SerializeToNode(record, ContractJson.Options)!, run: record.RunId);

    public void Append(string batch, ChecksRecord record) => AppendTask(batch, "checks", record.TaskId, JsonSerializer.SerializeToNode(record, ContractJson.Options)!, MaxChecksPerTask);

    public void Append(string batch, CiAttemptRecord record) =>
        AppendTask(batch, "ci_attempt", record.TaskId, JsonSerializer.SerializeToNode(record with { Detail = Cut(record.Detail, MaxDetail) ?? "" }, ContractJson.Options)!, MaxChecksPerTask);

    /// <summary>A line tagged <paramref name="type"/>. With <paramref name="perTask"/> the task's lines of this type are not written past that many; with <paramref name="run"/>, not when the file
    /// already holds a line of this type for that run id.</summary>
    private void AppendTask(string batch, string type, string task, JsonNode node, int? perTask = null, string? run = null)
    {
        if (!IsName(batch))
            return;
        node["type"] = type;
        lock (_gate)
        {
            var count = CountOf(_taskLineCounts, batch, TasksFile);
            if (count >= MaxTaskLines)
                return;
            var key = (batch, type, task);
            var of = perTask is null ? 0 : _perTask.TryGetValue(key, out var seen) ? seen : CountLines(batch, type, task);
            if (perTask is { } cap && of >= cap)
                return;
            if (run is not null && HasRun(batch, type, run))
                return;
            Write(batch, TasksFile, node.ToJsonString());
            _taskLineCounts[batch] = count + 1;
            if (perTask is not null)
                _perTask[key] = of + 1;
        }
    }

    /// <summary>The task records of a batch; empty when there are none.</summary>
    public BatchTaskRecords ReadTasks(string batch)
    {
        if (!IsName(batch))
            return BatchTaskRecords.Empty;
        var path = Path.Combine(Dir(batch), TasksFile);
        List<SessionRecord> sessions = [];
        List<GovernorRecord> governor = [];
        List<ChecksRecord> checks = [];
        List<CiAttemptRecord> attempts = [];
        lock (_gate)
        {
            if (!File.Exists(path))
                return BatchTaskRecords.Empty;
            foreach (var line in Lines(path))
            {
                try
                {
                    var node = JsonNode.Parse(line)!.AsObject();
                    switch (node["type"]?.GetValue<string>())
                    {
                        case "session" when node.Deserialize<SessionRecord>(ContractJson.Options) is { } s:
                            sessions.Add(s);
                            break;
                        case "governor" when node.Deserialize<GovernorRecord>(ContractJson.Options) is { } g:
                            governor.Add(g);
                            break;
                        case "checks" when node.Deserialize<ChecksRecord>(ContractJson.Options) is { } c:
                            checks.Add(c);
                            break;
                        case "ci_attempt" when node.Deserialize<CiAttemptRecord>(ContractJson.Options) is { } a:
                            attempts.Add(a);
                            break;
                    }
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException)
                {
                }
            }
        }
        return new BatchTaskRecords(sessions, governor, checks, attempts);
    }

    /// <summary>Deletes the directories of the oldest batches: past <see cref="MaxBatches"/>, or idle longer than <see cref="MaxAge"/>. A batch in <paramref name="running"/> is never deleted.</summary>
    /// <returns>The batch ids removed.</returns>
    public IReadOnlyList<string> Prune(IReadOnlySet<string> running)
    {
        if (!System.IO.Directory.Exists(directory))
            return [];
        var now = Time.GetUtcNow();
        var found = new DirectoryInfo(directory).EnumerateDirectories().Where(d => IsName(d.Name) && !running.Contains(d.Name))
            .Select(d => (d.Name, d.FullName, Written: new DateTimeOffset(LastWrite(d), TimeSpan.Zero))).OrderByDescending(d => d.Written).ToList();
        var room = Math.Max(0, MaxBatches - running.Count);
        var doomed = found.Where((d, i) => i >= room || now - d.Written > MaxAge).ToList();
        List<string> removed = [];
        lock (_gate)
            foreach (var d in doomed)
            {
                try
                {
                    System.IO.Directory.Delete(d.FullName, recursive: true);
                    removed.Add(d.Name);
                    _eventCounts.Remove(d.Name);
                    _taskLineCounts.Remove(d.Name);
                    foreach (var key in _perTask.Keys.Where(k => k.Batch == d.Name).ToList())
                        _perTask.Remove(key);
                }
                catch (IOException)
                {
                    // A directory in use is pruned at the next pass.
                }
            }
        return removed;
    }

    private static DateTime LastWrite(DirectoryInfo d)
    {
        var files = d.EnumerateFiles().Select(f => f.LastWriteTimeUtc).ToList();
        return files.Count == 0 ? d.LastWriteTimeUtc : files.Max();
    }

    private int CountOf(Dictionary<string, int> counts, string batch, string file)
    {
        if (counts.TryGetValue(batch, out var known))
            return known;
        var path = Path.Combine(Dir(batch), file);
        return counts[batch] = File.Exists(path) ? Lines(path).Length : 0;
    }

    private int CountLines(string batch, string type, string task)
    {
        var path = Path.Combine(Dir(batch), TasksFile);
        return File.Exists(path) ? Lines(path).Count(l => l.Contains($"\"type\":\"{type}\"", StringComparison.Ordinal) && l.Contains($"\"task_id\":{JsonSerializer.Serialize(task)}", StringComparison.Ordinal)) : 0;
    }

    private bool HasRun(string batch, string type, string run)
    {
        var path = Path.Combine(Dir(batch), TasksFile);
        return File.Exists(path) && Lines(path).Any(l => l.Contains($"\"type\":\"{type}\"", StringComparison.Ordinal) && l.Contains($"\"run_id\":{JsonSerializer.Serialize(run)}", StringComparison.Ordinal));
    }

    private void Write(string batch, string file, string line)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Dir(batch));
            using var stream = new FileStream(Path.Combine(Dir(batch), file), FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A record that cannot be written must not fail a batch.
            Console.Error.WriteLine($"chargehand: could not write {file} of batch {batch}: {e.Message}");
        }
    }

    /// <summary>The complete lines of a file: a last line with no newline is still being written.</summary>
    private static string[] Lines(string path)
    {
        string text;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return text[..(text.LastIndexOf('\n') + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? Cut(string? text, int max) => text is null || text.Length <= max ? text : text[..max] + " [cut]";

    /// <summary>A run id is a plain name; anything else would leave the records directory.</summary>
    private static bool IsName(string batch) => batch.Length is > 0 and <= 80 && batch.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
