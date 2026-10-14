using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Runners.Api;
using Chargehand.Tests;

namespace Chargehand.Modules.Runners.Tests;

/// <summary>ADR 0058, with real git: the repository at the pinned commit as one bundle, made once, from a checkout detached at that commit.</summary>
public sealed class RepositoryBundlesTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _source;
    private readonly string _bundles;
    private readonly string _first;
    private readonly string _second;

    public RepositoryBundlesTests()
    {
        _source = Path.Combine(_dir.Path, "checkout");
        _bundles = Path.Combine(_dir.Path, "bundles");
        Directory.CreateDirectory(_source);
        TestGit.Run(_source, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(_source, "a.txt"), "one\n");
        TestGit.Run(_source, "add", "-A");
        TestGit.Run(_source, "commit", "-q", "-m", "one");
        _first = TestGit.Run(_source, "rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(_source, "b.txt"), "two\n");
        TestGit.Run(_source, "add", "-A");
        TestGit.Run(_source, "commit", "-q", "-m", "two");
        _second = TestGit.Run(_source, "rev-parse", "HEAD").Trim();
        TestGit.Run(_source, "checkout", "-q", "--detach", _second);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task The_bundle_holds_the_commits_history_and_a_clone_of_it_can_be_checked_out_at_the_commit()
    {
        var path = await new RepositoryBundles(_bundles).BundleAsync(_source, _second, default);

        Assert.Equal(Path.Combine(_bundles, $"{_second}.bundle"), path);
        TestGit.Run(_source, "bundle", "verify", path);
        var clone = Path.Combine(_dir.Path, "clone");
        TestGit.Run(_dir.Path, "clone", "-q", "--no-checkout", path, clone);
        TestGit.Run(clone, "checkout", "-q", "-b", "chargehand/run1", _second);
        Assert.Equal(["two", "one"], TestGit.Run(clone, "log", "--format=%s").Trim().Split('\n'));
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(clone, "a.txt")));
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(clone, "b.txt")));
        // A bundle carries objects and refs: no configuration of the checkout, no hooks.
        Assert.False(File.Exists(Path.Combine(clone, ".git", "hooks", "pre-commit")));
    }

    [Fact]
    public async Task A_second_ask_for_the_same_commit_returns_the_same_file_without_making_it_again()
    {
        var bundles = new RepositoryBundles(_bundles);
        var path = await bundles.BundleAsync(_source, _second, default);
        var written = File.GetLastWriteTimeUtc(path);
        await Task.Delay(30);

        Assert.Equal(path, await bundles.BundleAsync(_source, _second, default));
        Assert.Equal(path, await new RepositoryBundles(_bundles).BundleAsync(_source, _second, default));   // another process finds the file too
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.Single(Directory.GetFiles(_bundles));
    }

    [Fact]
    public async Task Tasks_that_ask_at_once_get_one_bundle()
    {
        var bundles = new RepositoryBundles(_bundles);

        var paths = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => bundles.BundleAsync(_source, _second, default)));

        Assert.Single(paths.Distinct());
        Assert.Single(Directory.GetFiles(_bundles));
    }

    [Fact]
    public async Task One_callers_cancel_does_not_fail_another_that_waits_for_the_same_bundle()
    {
        var bundles = new RepositoryBundles(_bundles);
        using var cancel = new CancellationTokenSource();
        var first = bundles.BundleAsync(_source, _second, cancel.Token);
        var second = bundles.BundleAsync(_source, _second, default);

        await cancel.CancelAsync();

        Assert.True(File.Exists(await second));
        try
        {
            await first;      // it finished before the cancel, or it stopped waiting; it never faults
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task A_checkout_that_is_not_at_the_commit_is_not_bundled_and_leaves_nothing_behind()
    {
        TestGit.Run(_source, "checkout", "-q", "--detach", _first);
        var bundles = new RepositoryBundles(_bundles);

        var e = await Assert.ThrowsAsync<ChargehandException>(() => bundles.BundleAsync(_source, _second, default));

        Assert.Equal(ErrorCode.SessionFailed, e.Code);
        Assert.Contains("is not at commit", e.Message);
        Assert.False(Directory.Exists(_bundles) && Directory.GetFiles(_bundles).Length > 0);
        // The failure is not kept: once the checkout is right the same object makes it.
        TestGit.Run(_source, "checkout", "-q", "--detach", _second);
        Assert.True(File.Exists(await bundles.BundleAsync(_source, _second, default)));
    }

    [Fact]
    public async Task A_bundle_over_the_limit_is_not_kept()
    {
        var e = await Assert.ThrowsAsync<ChargehandException>(() => new RepositoryBundles(_bundles, maxBytes: 64).BundleAsync(_source, _second, default));

        Assert.Contains("above the", e.Message);
        Assert.Empty(Directory.GetFiles(_bundles));
    }

    [Fact]
    public async Task A_bundle_older_than_the_keep_time_is_made_again_and_older_bundles_of_other_commits_are_removed()
    {
        var old = Path.Combine(_bundles, "0000000000000000000000000000000000000000.bundle");
        Directory.CreateDirectory(_bundles);
        File.WriteAllText(old, "stale");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
        var stale = Path.Combine(_bundles, $"{_second}.bundle");
        File.WriteAllText(stale, "also stale");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        var path = await new RepositoryBundles(_bundles).BundleAsync(_source, _second, default);

        Assert.Equal(stale, path);
        TestGit.Run(_source, "bundle", "verify", path);                 // replaced by a real one
        Assert.False(File.Exists(old));
    }
}
