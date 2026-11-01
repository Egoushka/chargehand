using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.PeerClones.Api;

namespace Chargehand.Tests;

/// <summary>Issue #683: a public repository outside repository_roots is read through a shallow, size-checked clone that
/// is deleted when the run ends, one at a time.</summary>
public class PeerCloneManagerTests
{
    [Fact]
    public async Task A_repository_over_the_size_limit_is_refused_before_anything_is_cloned()
    {
        using var cache = new TempDir();
        using var remote = new TempDir();
        var repo = Runs.GitRepo(remote.Path);
        var settings = new PeerCloneSettings(cache.Path, MaxRepositoryBytes: 100, TokenSecret: "t");
        using var manager = new PeerCloneManager(settings, new FakeSize(1_000), (_, _) => Task.FromResult(repo.Path), _ => long.MaxValue);

        var e = await Assert.ThrowsAsync<ChargehandException>(() => manager.AcquireAsync("owner/name", repo.Commit, CancellationToken.None));

        Assert.Equal(ErrorCode.RepositoryNotAllowed, e.Code);
        Assert.Contains("1,000 bytes", e.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(cache.Path));
    }

    [Fact]
    public async Task A_cache_filesystem_under_the_free_space_minimum_is_refused_before_anything_is_cloned()
    {
        using var cache = new TempDir();
        using var remote = new TempDir();
        var repo = Runs.GitRepo(remote.Path);
        var settings = new PeerCloneSettings(cache.Path, MinFreeBytes: 1_000_000_000_000, TokenSecret: "t");
        using var manager = new PeerCloneManager(settings, new FakeSize(100), (_, _) => Task.FromResult(repo.Path), _ => 10);

        var e = await Assert.ThrowsAsync<ChargehandException>(() => manager.AcquireAsync("owner/name", repo.Commit, CancellationToken.None));

        Assert.Equal(ErrorCode.RepositoryNotAllowed, e.Code);
        Assert.Empty(Directory.GetFileSystemEntries(cache.Path));
    }

    [Fact]
    public async Task A_clone_is_deleted_when_its_lease_is_disposed()
    {
        using var cache = new TempDir();
        using var remote = new TempDir();
        var repo = Runs.GitRepo(remote.Path);
        var settings = new PeerCloneSettings(cache.Path, TokenSecret: "t");
        using var manager = new PeerCloneManager(settings, new FakeSize(100), (_, _) => Task.FromResult(repo.Path), _ => long.MaxValue);

        var lease = await manager.AcquireAsync("owner/name", repo.Commit, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(lease.Directory, "README.md")));

        await lease.DisposeAsync();

        Assert.False(Directory.Exists(lease.Directory));
    }

    [Fact]
    public async Task One_peer_read_runs_at_a_time()
    {
        using var cache = new TempDir();
        using var remote = new TempDir();
        var repo = Runs.GitRepo(remote.Path);
        var settings = new PeerCloneSettings(cache.Path, TokenSecret: "t");
        using var manager = new PeerCloneManager(settings, new FakeSize(100), (_, _) => Task.FromResult(repo.Path), _ => long.MaxValue);

        var first = await manager.AcquireAsync("owner/first", repo.Commit, CancellationToken.None);
        // The gate is held until first is disposed: the second call suspends on it synchronously, with nothing to wait for.
        var secondTask = manager.AcquireAsync("owner/second", repo.Commit, CancellationToken.None);
        Assert.False(secondTask.IsCompleted);

        await first.DisposeAsync();
        var second = await secondTask;
        Assert.True(Directory.Exists(second.Directory));
        await second.DisposeAsync();
    }

    private sealed class FakeSize(long bytes) : IPeerRepositorySize
    {
        public Task<long?> DiskUsageBytesAsync(string owner, string name, CancellationToken ct) => Task.FromResult<long?>(bytes);
    }
}

/// <summary>The orchestrator's own wiring: gh:owner/name resolves to a clone only for an account with public_clone, and the
/// clone is gone after the run, whatever the outcome.</summary>
public class PeerCloneOrchestratorTests
{
    [Fact]
    public async Task Gh_path_is_refused_for_an_account_without_public_clone()
    {
        using var root = new TempDir();
        using var remote = new TempDir();
        var repo = Runs.GitRepo(remote.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var profile = Runs.Profile(root.Path) with
        {
            Http = new Chargehand.Config.HttpSettings("key", Accounts: [new AccountSettings("peer-harvest")]),
        };
        var orchestrator = new Orchestrator(profile, runtime, "2.0.16", Repo.Root, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), new Dictionary<string, int>());

        var r = await orchestrator.RunAsync(Runs.CheapRequest(new RepositoryRef($"gh:owner/name", repo.Commit)), CancellationToken.None, actor: "peer-harvest");

        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.Equal(ErrorCode.RepositoryNotAllowed, r.Error?.Code);
        Assert.Empty(runtime.Created);
    }

    [Fact]
    public async Task Gh_path_is_refused_with_no_peer_clone_configured_even_for_a_permitted_account()
    {
        using var root = new TempDir();
        using var remote = new TempDir();
        var repo = Runs.GitRepo(remote.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var profile = Runs.Profile(root.Path) with
        {
            Http = new Chargehand.Config.HttpSettings("key", Accounts: [new AccountSettings("peer-harvest", PublicClone: true)]),
        };
        var orchestrator = new Orchestrator(profile, runtime, "2.0.16", Repo.Root, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), new Dictionary<string, int>());

        var r = await orchestrator.RunAsync(Runs.CheapRequest(new RepositoryRef($"gh:owner/name", repo.Commit)), CancellationToken.None, actor: "peer-harvest");

        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.Equal(ErrorCode.RepositoryNotAllowed, r.Error?.Code);
        Assert.Empty(runtime.Created);
    }

    [Fact]
    public async Task A_permitted_account_reads_a_gh_path_through_a_clone_that_is_deleted_after_the_run()
    {
        using var root = new TempDir();
        using var remote = new TempDir();
        using var cache = new TempDir();
        var repo = Runs.GitRepo(remote.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var peerClone = new PeerCloneSettings(cache.Path, TokenSecret: "t");
        var profile = Runs.Profile(root.Path) with
        {
            Http = new Chargehand.Config.HttpSettings("key", Accounts: [new AccountSettings("peer-harvest", PublicClone: true)]),
            PeerClone = peerClone,
        };
        using var manager = new PeerCloneManager(peerClone, new FakeSize(100), (_, _) => Task.FromResult(repo.Path), _ => long.MaxValue);
        var orchestrator = new Orchestrator(profile, runtime, "2.0.16", Repo.Root, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), new Dictionary<string, int>(),
            peerClones: manager);

        var r = await orchestrator.RunAsync(Runs.CheapRequest(new RepositoryRef($"gh:owner/name", repo.Commit)), CancellationToken.None, actor: "peer-harvest");

        Assert.NotEqual(ResultStatus.Failed, r.Status);
        Assert.True(File.Exists(Path.Combine(Assert.Single(runtime.Created).Directory, "README.md")));
        Assert.Empty(Directory.GetFiles(cache.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task The_clone_is_deleted_even_when_the_run_fails_after_checkout()
    {
        using var root = new TempDir();
        using var remote = new TempDir();
        using var cache = new TempDir();
        var repo = Runs.GitRepo(remote.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        runtime.CreateFailures.Enqueue(new InvalidOperationException("the runtime could not start a worker"));
        var peerClone = new PeerCloneSettings(cache.Path, TokenSecret: "t");
        var profile = Runs.Profile(root.Path) with
        {
            Http = new Chargehand.Config.HttpSettings("key", Accounts: [new AccountSettings("peer-harvest", PublicClone: true)]),
            PeerClone = peerClone,
        };
        using var manager = new PeerCloneManager(peerClone, new FakeSize(100), (_, _) => Task.FromResult(repo.Path), _ => long.MaxValue);
        var orchestrator = new Orchestrator(profile, runtime, "2.0.16", Repo.Root, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), new Dictionary<string, int>(),
            peerClones: manager);

        var r = await orchestrator.RunAsync(Runs.CheapRequest(new RepositoryRef($"gh:owner/name", repo.Commit)), CancellationToken.None, actor: "peer-harvest");

        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.Empty(Directory.GetFiles(cache.Path, "*", SearchOption.AllDirectories));
    }

    private sealed class FakeSize(long bytes) : IPeerRepositorySize
    {
        public Task<long?> DiskUsageBytesAsync(string owner, string name, CancellationToken ct) => Task.FromResult<long?>(bytes);
    }
}
