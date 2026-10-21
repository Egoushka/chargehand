using System.Text.Json;

namespace Chargehand.Automation;

/// <summary>The on/off switch of each watched repository (CHARGEHAND-241), kept beside intake's state in <c>intake/sources.json</c> and written whole through a temporary file. The profile says what
/// may be watched; this says what is watched now. A repository is on until it is switched off. Off, intake reads nothing of it and starts nothing for it: its labels and comments wait, and are read
/// when it is switched on again. Batches already running still report. Intake's poller and the server's <c>PUT /v1/intake/sources</c> share one instance, so every read and write takes its lock.</summary>
public sealed class IntakeSources
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    private readonly string? _path;
    private readonly HashSet<string> _off = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public IntakeSources()
    {
    }

    private IntakeSources(string path, IEnumerable<string> off)
    {
        _path = path;
        _off.UnionWith(off);
    }

    private sealed record Stored(List<string>? Off);

    public static IntakeSources Load(string path) =>
        new(path, File.Exists(path) ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json)?.Off ?? [] : []);

    public bool IsOn(string repository)
    {
        lock (_lock)
            return !_off.Contains(repository);
    }

    /// <returns>Whether the switch changed.</returns>
    public bool Set(string repository, bool on)
    {
        lock (_lock)
        {
            var changed = on ? _off.Remove(repository) : _off.Add(repository);
            if (changed && _path is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                var temp = $"{_path}.tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new Stored([.. _off.Order(StringComparer.OrdinalIgnoreCase)]), Json));
                File.Move(temp, _path, overwrite: true);
            }
            return changed;
        }
    }
}
