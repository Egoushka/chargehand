using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Chargehand.Modules.Tasks.Api;

/// <summary>What <see cref="TaskRecordStore.Read"/> read for one batch.</summary>
public sealed record BatchTaskRecords(IReadOnlyList<SessionRecord> Sessions, IReadOnlyList<GovernorRecord> Governor, IReadOnlyList<ChecksRecord> Checks, IReadOnlyList<CiAttemptRecord> Attempts)
{
    public static BatchTaskRecords Empty { get; } = new([], [], [], []);
}

/// <summary>The records the console keeps of a batch's tasks (CHARGEHAND-214): <c>tasks.jsonl</c> in the batch's directory, a line per session, governor log, check snapshot and CI fix attempt, tagged
/// by <c>type</c>. Append-only, written by the server process only, and bounded: <see cref="MaxTaskLines"/> lines per batch and <see cref="MaxChecksPerTask"/> check snapshots and fix attempts per task.
/// Nothing here is a secret or a prompt: counts, names, times and the governor's own log.</summary>
/// <param name="directory">The records directory, one directory per batch below it; the batch's events sit beside this file.</param>
public sealed class TaskRecordStore(string directory)
{
    public const string TasksFile = "tasks.jsonl";

    /// <summary>Lines of <c>tasks.jsonl</c> of one batch.</summary>
    public const int MaxTaskLines = 3000;

    public const int MaxDecisionsPerSession = GovernorRecord.MaxDecisions;

    /// <summary>Check snapshots and CI fix attempts kept per task.</summary>
    public const int MaxChecksPerTask = 60;

    /// <summary>The longest text a line keeps; a longer one is cut.</summary>
    public const int MaxDetail = 1500;

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _lineCounts = [];
    private readonly Dictionary<(string Batch, string Type, string Task), int> _perTask = [];

    private string Dir(string batch) => Path.Combine(directory, batch);

    /// <summary>A session's line, once per run id: a task taken up after its session ended reads that session's outcome again (ADR 0043), and the line already written stands.</summary>
    public void Append(string batch, SessionRecord record) => Append(batch, "session", record.TaskId, JsonSerializer.SerializeToNode(record, TaskRecordsJsonContext.Default.SessionRecord)!, run: record.RunId);

    /// <summary>A session's governor log, once per run id (see the session's line).</summary>
    public void Append(string batch, GovernorRecord record) => Append(batch, "governor", record.TaskId, JsonSerializer.SerializeToNode(record, TaskRecordsJsonContext.Default.GovernorRecord)!, run: record.RunId);

    public void Append(string batch, ChecksRecord record) => Append(batch, "checks", record.TaskId, JsonSerializer.SerializeToNode(record, TaskRecordsJsonContext.Default.ChecksRecord)!, MaxChecksPerTask);

    public void Append(string batch, CiAttemptRecord record) =>
        Append(batch, "ci_attempt", record.TaskId, JsonSerializer.SerializeToNode(record with { Detail = Cut(record.Detail, MaxDetail) }, TaskRecordsJsonContext.Default.CiAttemptRecord)!, MaxChecksPerTask);

    /// <summary>A line tagged <paramref name="type"/>. With <paramref name="perTask"/> the task's lines of this type are not written past that many; with <paramref name="run"/>, not when the file
    /// already holds a line of this type for that run id.</summary>
    private void Append(string batch, string type, string task, JsonNode node, int? perTask = null, string? run = null)
    {
        if (!IsName(batch))
            return;
        node["type"] = type;
        lock (_gate)
        {
            var count = CountOf(batch);
            if (count >= MaxTaskLines)
                return;
            var key = (batch, type, task);
            var of = perTask is null ? 0 : _perTask.TryGetValue(key, out var seen) ? seen : CountLines(batch, type, task);
            if (perTask is { } cap && of >= cap)
                return;
            if (run is not null && HasRun(batch, type, run))
                return;
            Write(batch, node.ToJsonString());
            _lineCounts[batch] = count + 1;
            if (perTask is not null)
                _perTask[key] = of + 1;
        }
    }

    /// <summary>The task records of a batch; empty when there are none.</summary>
    public BatchTaskRecords Read(string batch)
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
                        case "session" when node.Deserialize(TaskRecordsJsonContext.Default.SessionRecord) is { } s:
                            sessions.Add(s);
                            break;
                        case "governor" when node.Deserialize(TaskRecordsJsonContext.Default.GovernorRecord) is { } g:
                            governor.Add(g);
                            break;
                        case "checks" when node.Deserialize(TaskRecordsJsonContext.Default.ChecksRecord) is { } c:
                            checks.Add(c);
                            break;
                        case "ci_attempt" when node.Deserialize(TaskRecordsJsonContext.Default.CiAttemptRecord) is { } a:
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

    /// <summary>Drops what is counted for a batch whose directory was deleted.</summary>
    public void Forget(string batch)
    {
        lock (_gate)
        {
            _lineCounts.Remove(batch);
            foreach (var key in _perTask.Keys.Where(k => k.Batch == batch).ToList())
                _perTask.Remove(key);
        }
    }

    private int CountOf(string batch)
    {
        if (_lineCounts.TryGetValue(batch, out var known))
            return known;
        var path = Path.Combine(Dir(batch), TasksFile);
        return _lineCounts[batch] = File.Exists(path) ? Lines(path).Length : 0;
    }

    private int CountLines(string batch, string type, string task)
    {
        var path = Path.Combine(Dir(batch), TasksFile);
        return File.Exists(path)
            ? Lines(path).Count(l => l.Contains($"\"type\":\"{type}\"", StringComparison.Ordinal) && l.Contains($"\"task_id\":{JsonSerializer.Serialize(task, TaskRecordsJsonContext.Default.String)}", StringComparison.Ordinal))
            : 0;
    }

    private bool HasRun(string batch, string type, string run)
    {
        var path = Path.Combine(Dir(batch), TasksFile);
        return File.Exists(path)
            && Lines(path).Any(l => l.Contains($"\"type\":\"{type}\"", StringComparison.Ordinal) && l.Contains($"\"run_id\":{JsonSerializer.Serialize(run, TaskRecordsJsonContext.Default.String)}", StringComparison.Ordinal));
    }

    private void Write(string batch, string line)
    {
        try
        {
            Directory.CreateDirectory(Dir(batch));
            using var stream = new FileStream(Path.Combine(Dir(batch), TasksFile), FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A record that cannot be written must not fail a batch.
            Console.Error.WriteLine($"chargehand: could not write {TasksFile} of batch {batch}: {e.Message}");
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

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + " [cut]";

    /// <summary>A run id is a plain name; anything else would leave the records directory.</summary>
    private static bool IsName(string batch) => batch.Length is > 0 and <= 80 && batch.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>The lines of <c>tasks.jsonl</c> without reflection (ADR 0060), written as the contract options wrote them: snake_case, nulls left out.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionRecord))]
[JsonSerializable(typeof(GovernorRecord))]
[JsonSerializable(typeof(ChecksRecord))]
[JsonSerializable(typeof(CiAttemptRecord))]
[JsonSerializable(typeof(string))]
internal sealed partial class TaskRecordsJsonContext : JsonSerializerContext;
