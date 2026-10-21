using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Automation;

public enum PlanePhase { Running, Done }

/// <summary>A task intake started for a Plane work item, from the batch's start to the comment that says how it ended.</summary>
/// <param name="Identifier">The item's number as people read it (<c>ABC-12</c>).</param>
public sealed record PlaneTask(string Project, string Item, string Identifier, string BatchRunId, string TaskId, DateTimeOffset Started, PlanePhase Phase = PlanePhase.Running);

/// <summary>What Plane intake remembers between polls and across a restart (ADR 0067), in one JSON file written whole through a temporary file. <see cref="Handled"/> holds every trigger acted on or
/// refused as <c>&lt;item id&gt;@&lt;updated_at&gt;</c> and only grows: moving an item out of the trigger state and back changes its update time, so it is a new trigger.</summary>
public sealed class PlaneState
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly string? _path;

    public PlaneState()
    {
    }

    private PlaneState(string path) => _path = path;

    public HashSet<string> Handled { get; init; } = [];

    /// <summary>Comments written about a trigger (a wait, a refusal), by key, so each is written once.</summary>
    public HashSet<string> Notified { get; init; } = [];

    public List<PlaneTask> Tasks { get; init; } = [];

    /// <summary>When triggered batches started, for the daily cap; entries older than a day are dropped when the state is saved.</summary>
    public List<DateTimeOffset> Starts { get; init; } = [];

    public static PlaneState Load(string path)
    {
        if (!File.Exists(path))
            return new PlaneState(path);
        var state = JsonSerializer.Deserialize<PlaneState>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path}: empty intake state");
        return new PlaneState(path) { Handled = state.Handled ?? [], Notified = state.Notified ?? [], Tasks = state.Tasks ?? [], Starts = state.Starts ?? [] };
    }

    public void Replace(PlaneTask old, PlaneTask updated) => Tasks[Tasks.IndexOf(old)] = updated;

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
