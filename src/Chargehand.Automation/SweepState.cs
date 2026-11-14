using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Automation;

internal sealed record SweepEntry(string Hash, SweepVerdict Verdict, string Size, string Reason);

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

    public Task PutAsync(string key, string hash, SortResult r)
    {
        lock (_gate)
        {
            var entries = new Dictionary<string, SweepEntry>(Load()) { [key] = new SweepEntry(hash, r.Verdict, r.Size, r.Reason) };
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
        return Task.CompletedTask;
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
