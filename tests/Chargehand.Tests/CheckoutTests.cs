using Chargehand.Contracts;
using Chargehand.RunLog;

namespace Chargehand.Tests;

/// <summary>
/// ADR 0023: the worker reads a clone of the caller's repository at the pinned commit, made under worker_root, from a
/// source under the profile's repository roots. ADR 0006: OpenCode's grep reaches any file it is pointed at, so the
/// clone may hold no file its preset denies reading.
/// </summary>
public class CheckoutTests
{
    [Fact]
    public async Task A_tracked_denied_file_fails_before_any_worker_session()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        Directory.CreateDirectory(Path.Combine(repo.Path, "sub"));
        foreach (var name in new[] { "sub/.env", "x.env.local", ".env.example" })
            File.WriteAllText(Path.Combine(repo.Path, name), "TOKEN=placeholder\n");
        repo = Runs.Commit(repo, "sub/.env", "x.env.local", ".env.example");
        var runtime = new ScriptedRuntime(Runs.WorkerReply);

        var r = await Runs.Orchestrator(runtime, root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")))
            .RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.Equal(ErrorCode.CheckoutHasSecrets, r.Error?.Code);
        Assert.NotNull(r.Error!.Action);
        Assert.Contains("sub/.env", r.Summary, StringComparison.Ordinal);
        Assert.Contains("x.env.local", r.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(".env.example", r.Summary, StringComparison.Ordinal);
        Assert.Empty(runtime.Created);
    }

    [Fact]
    public async Task Uncommitted_and_ignored_files_never_reach_the_worker()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        File.WriteAllText(Path.Combine(repo.Path, ".gitignore"), ".env*\n");
        File.WriteAllText(Path.Combine(repo.Path, ".env"), "TOKEN=placeholder\n");
        File.WriteAllText(Path.Combine(repo.Path, "README.md"), "edited, not committed\n");
        var runtime = new ScriptedRuntime(Runs.WorkerReply);

        var r = await Runs.Orchestrator(runtime, root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")))
            .RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        Assert.NotEqual(ResultStatus.Failed, r.Status);
        var directory = Assert.Single(runtime.Created).Directory;
        Assert.NotEqual(Path.GetFullPath(repo.Path), directory);
        Assert.False(File.Exists(Path.Combine(directory, ".env")));
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(directory, "README.md")));
    }

    [Fact]
    public async Task A_second_run_reuses_the_clone()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var orchestrator = Runs.Orchestrator(runtime, root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")));

        await orchestrator.RunAsync(Runs.CheapRequest(repo with { Commit = repo.Commit[..7] }), CancellationToken.None);
        await orchestrator.RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        Assert.Single(runtime.Created.Select(s => s.Directory).Distinct());
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(runtime.Created.First().Directory)!));
    }

    [Fact]
    public async Task A_repository_outside_the_roots_is_refused()
    {
        using var root = new TempDir();
        using var elsewhere = new TempDir();
        var repo = Runs.GitRepo(elsewhere.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);

        var r = await Runs.Orchestrator(runtime, root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")))
            .RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.Contains("repository_roots", r.Summary, StringComparison.Ordinal);
        Assert.Empty(runtime.Created);
    }

    [Fact]
    public async Task A_root_of_slash_allows_any_repository()
    {
        using var root = new TempDir();
        using var elsewhere = new TempDir();
        var repo = Runs.GitRepo(elsewhere.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var orchestrator = new Orchestrator(Runs.Profile(root.Path) with { RepositoryRoots = ["/"] }, runtime, "2.0.16", Repo.Root,
            new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), new Dictionary<string, int>());

        var r = await orchestrator.RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        Assert.NotEqual(ResultStatus.Failed, r.Status);
        Assert.StartsWith(Path.Combine(root.Path, ".checkouts"), Assert.Single(runtime.Created).Directory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_checkout_outside_the_worker_root_is_not_allowed()
    {
        using var dir = new TempDir();
        var repo = Runs.GitRepo(dir.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);

        var r = await Runs.Orchestrator(runtime, Path.Combine(dir.Path, "root"), new JsonlRunLog(Path.Combine(dir.Path, "log.jsonl")))
            .RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        Assert.Equal(new ResultError(ErrorCode.RepositoryNotAllowed, r.Summary, false), r.Error);
        Assert.Empty(runtime.Created);
    }

    [Fact]
    public async Task A_checkout_at_another_commit_is_invalid()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);

        var r = await Runs.Orchestrator(new ScriptedRuntime(Runs.WorkerReply), root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")))
            .RunAsync(Runs.CheapRequest(repo with { Commit = "0000000" }), CancellationToken.None);

        Assert.Equal(ErrorCode.CheckoutInvalid, r.Error?.Code);
        Assert.NotNull(r.Error!.Action);
    }
}
