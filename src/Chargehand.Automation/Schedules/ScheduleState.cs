using System.Text.Json;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Automation;

/// <summary>A run the scheduler started and has not seen end.</summary>
/// <param name="Attempts">Posts of the result that failed; the result is dropped after <see cref="Scheduler.MaxPostAttempts"/>.</param>
public sealed record ScheduledRun(string Entry, string RunId, DateTimeOffset Started, ScheduleKind Kind, int Attempts = 0);

/// <summary>What the scheduler remembers across a restart (ADR 0068), in one JSON file beside the run log written whole through a temporary file: per entry the moment up to which its occurrences are dealt with,
/// the starts in the current month, and the runs not yet seen to end.</summary>
public sealed class ScheduleState
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    private readonly string? _path;

    public ScheduleState()
    {
    }

    private ScheduleState(string path) => _path = path;

    /// <summary>Per entry, the time its due occurrences were last dealt with (run, skipped for the cap, or skipped as still running): the next one is the first after it. A slot missed while the server was down is one occurrence, not one per slot.</summary>
    public Dictionary<string, DateTimeOffset> Handled { get; init; } = [];

    /// <summary>Per entry, starts by calendar month (UTC, <c>yyyy-MM</c>); months before the current one are dropped when the state is saved.</summary>
    public Dictionary<string, Dictionary<string, int>> Starts { get; init; } = [];

    public List<ScheduledRun> Running { get; init; } = [];

    public static string MonthOf(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    public int StartsIn(string entry, DateTimeOffset at) => Starts.GetValueOrDefault(entry)?.GetValueOrDefault(MonthOf(at)) ?? 0;

    public void CountStart(string entry, DateTimeOffset at)
    {
        if (!Starts.TryGetValue(entry, out var months))
            Starts[entry] = months = [];
        months[MonthOf(at)] = months.GetValueOrDefault(MonthOf(at)) + 1;
    }

    public static ScheduleState Load(string path)
    {
        if (!File.Exists(path))
            return new ScheduleState(path);
        var state = JsonSerializer.Deserialize<ScheduleState>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path}: empty schedule state");
        return new ScheduleState(path) { Handled = state.Handled ?? [], Starts = state.Starts ?? [], Running = state.Running ?? [] };
    }

    public void Save(DateTimeOffset now)
    {
        var month = MonthOf(now);
        foreach (var months in Starts.Values)
            foreach (var old in months.Keys.Where(m => string.CompareOrdinal(m, month) < 0).ToList())
                months.Remove(old);
        if (_path is null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temp = $"{_path}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, _path, overwrite: true);
    }
}
