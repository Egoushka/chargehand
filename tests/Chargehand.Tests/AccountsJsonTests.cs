using System.Text.Json;
using System.Text.Json.Serialization;
using Chargehand.Modules.Accounts.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0060: the keys file and the key-use file are written without reflection, byte for byte as the reflection options wrote them.</summary>
public sealed class AccountsJsonTests : IDisposable
{
    private static readonly JsonSerializerOptions KeysBefore = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true };
    private static readonly JsonSerializerOptions UseBefore = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    private readonly string _dir = Directory.CreateTempSubdirectory("accounts-json").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void The_keys_file_reads_and_writes_as_before()
    {
        var path = Path.Combine(_dir, "accounts.json");
        var store = new AccountKeyStore(path);
        var (id, _) = store.Create("alice", "laptop");
        store.Create("bob", null);
        store.Revoke(id);

        var written = File.ReadAllText(path);
        var before = JsonSerializer.Serialize(JsonSerializer.Deserialize<List<AccountKey>>(written, KeysBefore), KeysBefore);

        Assert.Equal(before, written);
        Assert.Equal(2, new AccountKeyStore(path).List().Count);
    }

    [Fact]
    public void The_key_use_file_reads_and_writes_as_before()
    {
        var path = Path.Combine(_dir, "accounts-use.json");
        new AccountKeyUse(path).Record("k1");

        var written = File.ReadAllText(path);
        var before = JsonSerializer.Serialize(JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(written, UseBefore), UseBefore);

        Assert.Equal(before, written);
        Assert.Contains("k1", new AccountKeyUse(path).Read().Keys);
    }
}
