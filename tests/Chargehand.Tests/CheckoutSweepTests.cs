using Chargehand.Workspace;

namespace Chargehand.Tests;

/// <summary>issue #509: a clone per (repository, commit) under worker_root/.checkouts is otherwise kept forever.</summary>
public class CheckoutSweepTests
{
    [Fact]
    public void Prune_removes_the_oldest_clones_past_the_count()
    {
        using var dir = new TempDir();
        var clock = new ManualTimeProvider();
        var now = clock.GetUtcNow().UtcDateTime;
        for (var i = 0; i < CheckoutSweep.MaxCheckouts; i++)
            Seed(dir, $"repo-{i:D4}", now.AddMinutes(-i - 1));
        Seed(dir, "repo-oldest", now.AddMinutes(-CheckoutSweep.MaxCheckouts - 5));
        Seed(dir, "repo-older", now.AddMinutes(-CheckoutSweep.MaxCheckouts - 6));

        var removed = CheckoutSweep.Prune(dir.Path, clock);

        Assert.Equal(2, removed.Count);
        Assert.True(File.Exists(Path.Combine(dir.Path, ".checkouts", "repo-0000", "abc123", "README")));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, ".checkouts", "repo-oldest")));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, ".checkouts", "repo-older")));
    }

    [Fact]
    public void Prune_removes_a_clone_idle_past_the_age_and_leaves_a_fresh_one()
    {
        using var dir = new TempDir();
        var clock = new ManualTimeProvider();
        var now = clock.GetUtcNow().UtcDateTime;
        Seed(dir, "repo-fresh", now.AddMinutes(-5));
        Seed(dir, "repo-stale", now.AddDays(-200));

        var removed = CheckoutSweep.Prune(dir.Path, clock);

        Assert.Equal([Path.Combine(dir.Path, ".checkouts", "repo-stale", "abc123")], removed);
        Assert.True(Directory.Exists(Path.Combine(dir.Path, ".checkouts", "repo-fresh", "abc123")));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, ".checkouts", "repo-stale")));
    }

    [Fact]
    public void Prune_does_nothing_without_a_checkouts_directory()
    {
        using var dir = new TempDir();

        Assert.Empty(CheckoutSweep.Prune(dir.Path));
    }

    private static void Seed(TempDir dir, string repo, DateTime written)
    {
        var file = dir.Write($".checkouts/{repo}/abc123/README", "clone");
        File.SetLastWriteTimeUtc(file, written);
    }
}
