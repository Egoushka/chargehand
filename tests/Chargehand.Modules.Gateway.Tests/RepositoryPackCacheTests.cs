using Chargehand.Modules.Gateway.Api;
using Chargehand.Tests;

namespace Chargehand.Modules.Gateway.Tests;

/// <summary>Issue #435: a repository context pack is cached per repository and commit, rebuilt only when the commit changes.</summary>
public class RepositoryPackCacheTests
{
    [Fact]
    public void A_second_call_on_the_same_commit_reuses_the_pack_instead_of_rescanning_the_directory()
    {
        using var dir = new TempDir();
        dir.Write("go.mod", "module x\n");
        var cache = new RepositoryPackCache();

        var first = cache.GetOrBuild("owner/repo", dir.Path, "c1");
        dir.Write("Cargo.toml", "[package]\n");
        var second = cache.GetOrBuild("owner/repo", dir.Path, "c1");

        Assert.Same(first, second);
        Assert.DoesNotContain("rust", second.Ecosystems.Select(e => e.Name));
    }

    [Fact]
    public void A_new_commit_of_the_same_repository_rebuilds_the_pack()
    {
        using var dir = new TempDir();
        dir.Write("go.mod", "module x\n");
        var cache = new RepositoryPackCache();

        var first = cache.GetOrBuild("owner/repo", dir.Path, "c1");
        dir.Write("Cargo.toml", "[package]\n");
        var second = cache.GetOrBuild("owner/repo", dir.Path, "c2");

        Assert.NotSame(first, second);
        Assert.Contains("rust", second.Ecosystems.Select(e => e.Name));
    }

    [Fact]
    public void Two_commits_of_the_same_repository_are_cached_independently()
    {
        using var dirA = new TempDir();
        dirA.Write("go.mod", "module x\n");
        using var dirB = new TempDir();
        dirB.Write("Cargo.toml", "[package]\n");
        var cache = new RepositoryPackCache();

        var a = cache.GetOrBuild("owner/repo", dirA.Path, "c1");
        var b = cache.GetOrBuild("owner/repo", dirB.Path, "c2");
        var aAgain = cache.GetOrBuild("owner/repo", dirA.Path, "c1");

        Assert.Contains("go", a.Ecosystems.Select(e => e.Name));
        Assert.Contains("rust", b.Ecosystems.Select(e => e.Name));
        Assert.Same(a, aAgain);
    }

    [Fact]
    public void Different_repositories_on_the_same_commit_hash_are_cached_independently()
    {
        using var dirA = new TempDir();
        dirA.Write("go.mod", "module x\n");
        using var dirB = new TempDir();
        dirB.Write("Cargo.toml", "[package]\n");
        var cache = new RepositoryPackCache();

        var a = cache.GetOrBuild("owner/repo-a", dirA.Path, "c1");
        var b = cache.GetOrBuild("owner/repo-b", dirB.Path, "c1");

        Assert.Contains("go", a.Ecosystems.Select(e => e.Name));
        Assert.Contains("rust", b.Ecosystems.Select(e => e.Name));
    }
}
