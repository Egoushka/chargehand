using System.Text.Json;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>CHARGEHAND-214: the time an account key last opened a request, kept in a file of its own beside the keys, written when a key has no time and then at most once a minute.</summary>
public class AccountKeyUseTests
{
    [Fact]
    public void The_first_use_is_written_at_once_and_later_ones_only_after_a_minute()
    {
        using var dir = new TempDir();
        var clock = new ManualTimeProvider();
        var use = new AccountKeyUse(Path.Combine(dir.Path, "use.json"), clock);
        var first = clock.GetUtcNow();

        use.Record("k1");
        clock.Advance(TimeSpan.FromSeconds(59));
        use.Record("k1");

        Assert.Equal(first, use.Read()["k1"]);
        clock.Advance(TimeSpan.FromSeconds(1));
        use.Record("k1");
        Assert.Equal(first.AddSeconds(60), use.Read()["k1"]);
    }

    [Fact]
    public void Each_key_has_its_own_time_and_a_new_process_carries_on_from_the_file()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "use.json");
        var clock = new ManualTimeProvider();
        new AccountKeyUse(path, clock).Record("k1");
        clock.Advance(TimeSpan.FromMinutes(5));
        var again = new AccountKeyUse(path, clock);

        again.Record("k2");

        var read = again.Read();
        Assert.Equal(["k1", "k2"], read.Keys.Order());
        Assert.Equal(TimeSpan.FromMinutes(5), read["k2"] - read["k1"]);
    }

    [Fact]
    public void A_file_that_is_not_readable_reads_as_no_use_and_does_not_stop_a_request()
    {
        using var dir = new TempDir();
        var path = dir.Write("use.json", "{ not json");
        var use = new AccountKeyUse(path, new ManualTimeProvider());

        Assert.Empty(use.Read());
        use.Record("k1");

        Assert.Contains("k1", use.Read().Keys);
    }

    [Fact]
    public void A_use_that_cannot_be_written_is_swallowed()
    {
        using var dir = new TempDir();
        var blocked = dir.Write("blocker", "a file where the directory should be");
        var use = new AccountKeyUse(Path.Combine(blocked, "use.json"), new ManualTimeProvider());

        use.Record("k1");

        Assert.Empty(use.Read());
    }

    [Fact]
    public void The_file_holds_ids_and_times_and_is_readable_by_its_owner_only()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "use.json");
        new AccountKeyUse(path, new ManualTimeProvider()).Record("k1");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(["k1"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("k1").ValueKind);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void The_use_file_sits_beside_the_keys_file()
    {
        Assert.Equal(Path.Combine(Path.GetFullPath("/srv/chargehand"), "accounts-use.json"), AccountKeyUse.FileFor("/srv/chargehand/accounts.json"));
    }
}
