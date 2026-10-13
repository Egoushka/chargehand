using System.Text.Json;

namespace Chargehand.Modules.Accounts.Api;

/// <summary>When each account key last opened a request (CHARGEHAND-214): its id and a time, nothing about the request, the caller or the address. The keys file is written by <c>chargehand accounts</c>, so
/// the times live in a file of their own beside it, which only the server writes. A key's time is written when it has none and then at most once a minute, so a busy key costs a write a minute and a
/// time read from the file can be up to a minute behind. A key that is revoked or never used has no entry.</summary>
public sealed class AccountKeyUse(string path, TimeProvider? clock = null)
{
    public static readonly TimeSpan WriteEvery = TimeSpan.FromMinutes(1);

    private readonly object _lock = new();
    private Dictionary<string, DateTimeOffset>? _times;
    private readonly Dictionary<string, DateTimeOffset> _written = [];

    /// <summary>The use file that goes with a keys file.</summary>
    public static string FileFor(string keysFile) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(keysFile))!, "accounts-use.json");

    public void Record(string keyId)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        lock (_lock)
        {
            _times ??= Load();
            _times[keyId] = now;
            if (_written.TryGetValue(keyId, out var last) && now - last < WriteEvery)
                return;
            _written[keyId] = now;
            Save(_times);
        }
    }

    /// <summary>Key id to the time it was last seen, as the file holds it; empty when there is no file or it cannot be read.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> Read()
    {
        lock (_lock)
            return Load();
    }

    private Dictionary<string, DateTimeOffset> Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), AccountsJsonContext.Default.DictionaryStringDateTimeOffset) ?? [] : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Save(Dictionary<string, DateTimeOffset> times)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temp, options))
            {
                JsonSerializer.Serialize(file, times, AccountsJsonContext.Default.DictionaryStringDateTimeOffset);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A time that cannot be written must not refuse a request.
            Console.Error.WriteLine($"chargehand: could not write {path}: {e.Message}");
        }
    }
}
