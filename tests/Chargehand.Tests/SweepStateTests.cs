using Chargehand.Automation;

namespace Chargehand.Tests;

public class SweepStateTests
{
    private static IntakeIssue Issue(params string[] labels) => new(1, "T", "B", "a", false, labels, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Cache_hit_when_only_sweep_labels_changed()
    {
        var plain = SweepState.Hash(Issue("bug", "ui"));
        Assert.Equal(plain, SweepState.Hash(Issue("ui", "agent:ready", "size:M", "bug")));
        Assert.NotEqual(plain, SweepState.Hash(Issue("bug")));
    }

    [Fact]
    public async Task Verdicts_persist_and_a_changed_hash_misses()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sweep-{Guid.NewGuid():N}", "intake", "sweep.json");
        try
        {
            var r = new SortResult(SweepVerdict.Blocked, "L", "waits");
            await new SweepState(path).PutAsync("owner/app#1", "h1", r);
            var reloaded = new SweepState(path);
            Assert.Equal(r, await reloaded.CachedAsync("owner/app#1", "h1"));
            Assert.Null(await reloaded.CachedAsync("owner/app#1", "h2"));
            Assert.Equal(SweepVerdict.Blocked, await reloaded.LastVerdictAsync("owner/app#1"));
            Assert.Null(await reloaded.LastVerdictAsync("owner/app#2"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(path))!, true);
        }
    }

    [Fact]
    public async Task A_reason_with_an_emoji_at_the_cut_round_trips()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sweep-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "sweep.json");
        try
        {
            var r = new SortResult(SweepVerdict.Ready, "S", new string('a', 199) + "\U0001F600");
            await new SweepState(path).PutAsync("owner/app#1", "h", r);
            Assert.Equal(r, await new SweepState(path).CachedAsync("owner/app#1", "h"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task A_failed_write_leaves_the_map_unchanged()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sweep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "sweep.json");
            var state = new SweepState(path);
            var ok = new SortResult(SweepVerdict.Ready, "S", "ok");
            await state.PutAsync("owner/app#1", "h", ok);
            Directory.CreateDirectory($"{path}.tmp");
            await Assert.ThrowsAnyAsync<Exception>(() => state.PutAsync("owner/app#2", "h", ok));
            Assert.Null(await state.LastVerdictAsync("owner/app#2"));
            Directory.Delete($"{path}.tmp");
            await state.PutAsync("owner/app#3", "h", ok);
            var fresh = new SweepState(path);
            Assert.Equal(ok, await fresh.CachedAsync("owner/app#1", "h"));
            Assert.Null(await fresh.LastVerdictAsync("owner/app#2"));
            Assert.Equal(ok, await fresh.CachedAsync("owner/app#3", "h"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
