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
}
