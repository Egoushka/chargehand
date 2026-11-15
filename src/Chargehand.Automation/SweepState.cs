using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Automation;

/// <param name="QueuedHash">The issue's hash when the sweep last queued it into the pool; null: never queued.</param>
internal sealed record SweepEntry(string Hash, SweepVerdict Verdict, string Size, string Reason, string? QueuedHash = null);

internal sealed record SweepFile(Dictionary<string, SweepEntry> Entries);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SweepFile))]
internal sealed partial class SweepStateJsonContext : JsonSerializerContext;

/// <summary>The sweep's verdicts by issue (<c>owner/repo#n</c>), in one JSON file written through a flushed temporary file and a rename,
/// so an issue whose text did not change is not sorted again. A damaged file is an empty cache: the next sweep sorts again.</summary>
public sealed class SweepState(string path)
{
    private readonly object _gate = new();
    private Dictionary<string, SweepEntry>? _entries;

    /// <summary>SHA-256 over title, body and sorted labels, without the sweep's own <c>agent:</c> and <c>size:</c> labels, so its writes do not invalidate the cache.</summary>
    public static string Hash(IntakeIssue issue)
    {
        var labels = issue.Labels.Where(l => !l.StartsWith("agent:", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);
        var text = string.Join('\0', new[] { issue.Title, issue.Body }.Concat(labels));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public Task<SortResult?> CachedAsync(string key, string hash)
    {
        lock (_gate)
            return Task.FromResult(Load().TryGetValue(key, out var e) && e.Hash == hash ? new SortResult(e.Verdict, e.Size, e.Reason) : null);
    }

    public Task<SweepVerdict?> LastVerdictAsync(string key)
    {
        lock (_gate)
            return Task.FromResult(Load().TryGetValue(key, out var e) ? e.Verdict : (SweepVerdict?)null);
    }

    /// <summary>The issue's hash when the sweep last queued it; null when it never did.</summary>
    public Task<string?> QueuedHashAsync(string key)
    {
        lock (_gate)
            return Task.FromResult(Load().TryGetValue(key, out var e) ? e.QueuedHash : null);
    }

    /// <summary>Records that the issue was queued at <paramref name="hash"/>, so the same text is not queued twice.</summary>
    public Task MarkQueuedAsync(string key, string hash)
    {
        lock (_gate)
        {
            if (Load().TryGetValue(key, out var e))
                Save(key, e with { QueuedHash = hash });
        }
        return Task.CompletedTask;
    }

    public Task PutAsync(string key, string hash, SortResult r)
    {
        lock (_gate)
        {
            var queued = Load().TryGetValue(key, out var e) ? e.QueuedHash : null;
            Save(key, new SweepEntry(hash, r.Verdict, r.Size, r.Reason, queued));
        }
        return Task.CompletedTask;
    }

    private void Save(string key, SweepEntry entry)
    {
        var entries = new Dictionary<string, SweepEntry>(Load()) { [key] = entry };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = $"{path}.tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new SweepFile(entries), SweepStateJsonContext.Default.SweepFile)));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
        _entries = entries;
    }

    private Dictionary<string, SweepEntry> Load()
    {
        if (_entries is not null)
            return _entries;
        try
        {
            _entries = File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), SweepStateJsonContext.Default.SweepFile)?.Entries : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            _entries = null;
        }
        return _entries ??= [];
    }
}
