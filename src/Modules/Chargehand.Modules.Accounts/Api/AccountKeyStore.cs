using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Chargehand.Modules.Accounts.Api;

/// <summary>One key of an account, as stored: never the key itself, only a salted SHA-256 of its secret half.</summary>
/// <param name="Id">The public half of the key (<c>chk1.&lt;id&gt;.&lt;secret&gt;</c>), what <c>accounts list</c> and <c>revoke</c> name.</param>
public sealed record AccountKey(string Id, string Account, string? Label, string Salt, string Hash, DateTimeOffset Created, DateTimeOffset? Revoked = null);

/// <summary>
/// The keys of the profile's accounts (ADR 0050), in one JSON file the server reads and <c>chargehand accounts</c> writes. A key is 32 random bytes, so a salted
/// SHA-256 is enough to keep a leaked file from yielding keys; the file is readable by its owner only. The server re-reads the file when it changes, so a revoked key stops
/// working without a restart.
/// </summary>
public sealed class AccountKeyStore(string path)
{
    public const string Prefix = "chk1";

    private readonly object _lock = new();
    private (DateTime Written, long Length) _stamp;
    private IReadOnlyList<AccountKey> _cached = [];

    /// <summary>Makes a key for <paramref name="account"/> and returns it with its id. This is the only time the key exists in the clear.</summary>
    public (string Id, string Key) Create(string account, string? label)
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (_lock)
            Save([.. List(), new AccountKey(id, account, label, salt, HashOf(salt, secret), DateTimeOffset.UtcNow)]);
        return (id, $"{Prefix}.{id}.{secret}");
    }

    /// <returns>False when no key has that id or it is already revoked.</returns>
    public bool Revoke(string id)
    {
        lock (_lock)
        {
            var keys = List().ToList();
            var at = keys.FindIndex(k => k.Id == id && k.Revoked is null);
            if (at < 0)
                return false;
            keys[at] = keys[at] with { Revoked = DateTimeOffset.UtcNow };
            Save(keys);
            return true;
        }
    }

    public IReadOnlyList<AccountKey> List()
    {
        lock (_lock)
        {
            if (!File.Exists(path))
                return _cached = [];
            var info = new FileInfo(path);
            if ((info.LastWriteTimeUtc, info.Length) != _stamp)
            {
                _cached = JsonSerializer.Deserialize(File.ReadAllText(path), AccountsJsonContext.Default.ListAccountKey) ?? [];
                _stamp = (info.LastWriteTimeUtc, info.Length);
            }
            return _cached;
        }
    }

    /// <summary>The live key a presented bearer value is, or null. The salted hash is computed and compared in constant time whether or not the id exists.</summary>
    public AccountKey? Authenticate(string presented)
    {
        var parts = presented.Split('.');
        if (parts.Length != 3 || parts[0] != Prefix || presented.Length > 200)
            return null;
        var known = List().FirstOrDefault(k => k.Id == parts[1]);
        var hash = HashOf(known?.Salt ?? Convert.ToHexStringLower(new byte[16]), parts[2]);
        var same = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(known?.Hash ?? new string('0', 64)));
        return same && known is { Revoked: null } ? known : null;
    }

    private static string HashOf(string salt, string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{salt}:{secret}")));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Written to a temporary file, flushed and renamed over the old one, readable by its owner only.</summary>
    private void Save(IReadOnlyList<AccountKey> keys)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new FileStream(temp, options))
        {
            JsonSerializer.Serialize(file, [.. keys], AccountsJsonContext.Default.ListAccountKey);
            file.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
        _stamp = default;
    }
}
