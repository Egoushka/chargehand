using System.Text.Json;

namespace Chargehand.Automation;

/// <summary>A notice and the sinks it reached.</summary>
public sealed record SentNotice(QuestionNotice Notice, List<string> Sinks);

/// <summary>A decision that came back and what it did: the run it started, or <c>dropped</c>.</summary>
public sealed record HandledDecision(string Outcome, DateTimeOffset At);

/// <summary>What the notifier and the answer route remember across a restart (ADR 0077), in one JSON file beside the run log written whole through a temporary file: the moment notices began (older
/// ones are never sent, so turning it on does not send every question of the last two weeks), the notices sent and where, and the decisions already acted on (a decision sent twice starts one run).
/// The notifier's loop and the answer route share it, so every access holds its lock.</summary>
public sealed class NotifyState
{
    public static readonly TimeSpan KeepDecisions = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    private readonly string? _path;
    private readonly Lock _gate = new();

    public NotifyState()
    {
    }

    private NotifyState(string path) => _path = path;

    public DateTimeOffset? Since { get; set; }

    public Dictionary<string, SentNotice> Sent { get; init; } = [];

    public Dictionary<string, HandledDecision> Decisions { get; init; } = [];

    public static NotifyState Load(string path)
    {
        if (!File.Exists(path))
            return new NotifyState(path);
        var state = JsonSerializer.Deserialize<NotifyState>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path}: empty notify state");
        return new NotifyState(path) { Since = state.Since, Sent = state.Sent ?? [], Decisions = state.Decisions ?? [] };
    }

    /// <summary>Runs <paramref name="change"/> under the lock and saves.</summary>
    public T Change<T>(DateTimeOffset now, Func<NotifyState, T> change)
    {
        lock (_gate)
        {
            var result = change(this);
            Save(now);
            return result;
        }
    }

    /// <summary>The notice is sent again to <paramref name="sink"/> at the next wake, one round on: the decision that came back carried no answer.</summary>
    public void SendAgain(string noticeId, string sink, DateTimeOffset now) => Change(now, s =>
    {
        if (s.Sent.TryGetValue(noticeId, out var sent))
            s.Sent[noticeId] = new SentNotice(sent.Notice with { Round = sent.Notice.Round + 1 }, [.. sent.Sinks.Where(k => k != sink)]);
        return true;
    });

    public T Read<T>(Func<NotifyState, T> read)
    {
        lock (_gate)
            return read(this);
    }

    private void Save(DateTimeOffset now)
    {
        foreach (var old in Decisions.Where(d => now - d.Value.At > KeepDecisions).Select(d => d.Key).ToList())
            Decisions.Remove(old);
        if (_path is null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temp = $"{_path}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, _path, overwrite: true);
    }
}
