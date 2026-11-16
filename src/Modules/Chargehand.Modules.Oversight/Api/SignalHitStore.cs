using System.Text.Json;

namespace Chargehand.Modules.Oversight.Api;

/// <summary>Where signal hits are kept: one JSON line per hit, appended, so a restart loses none and a reader never sees half a line it cannot skip. A hit the owner has judged carries <c>confirmed</c>,
/// the label P06's precision check counts.</summary>
public sealed class SignalHitStore(string path)
{
    private readonly Lock _gate = new();

    public void Append(SignalHit hit)
    {
        var line = Line(hit, null);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.AppendAllText(path, line + "\n");
        }
    }

    /// <summary>Every hit, oldest first; a line that does not parse is skipped.</summary>
    public IReadOnlyList<SignalHit> ReadAll()
    {
        lock (_gate)
        {
            if (!File.Exists(path))
                return [];
            var hits = new List<SignalHit>();
            foreach (var line in File.ReadLines(path))
            {
                if (Parse(line) is { } hit)
                    hits.Add(hit);
            }
            return hits;
        }
    }

    public static string Line(SignalHit hit, bool? confirmed) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["signal_id"] = hit.SignalId,
            ["run_id"] = hit.RunId,
            ["at"] = hit.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["reason"] = hit.Reason,
            ["confirmed"] = confirmed,
        });

    private static SignalHit? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            return e.TryGetProperty("signal_id", out var s) && e.TryGetProperty("run_id", out var r) && e.TryGetProperty("at", out var a) && e.TryGetProperty("reason", out var why)
                && s.GetString() is { } signal && r.GetString() is { } run && a.GetDateTimeOffset() is var at
                ? new SignalHit(signal, run, at, why.GetString() ?? "") : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
