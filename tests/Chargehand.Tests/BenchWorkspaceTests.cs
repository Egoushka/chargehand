using System.Diagnostics;
using Chargehand.Bench;

namespace Chargehand.Tests;

/// <summary>The checkouts a benchmark makes, with real git and a local directory standing in for the upstream: the clone is at the pin, the server finds its remote and default branch, and a
/// local remote takes only the branches a session may push.</summary>
public sealed class BenchWorkspaceTests : IDisposable
{
    private static readonly string[] Identity = ["-c", "user.name=t", "-c", "user.email=t@example.test", "-c", "commit.gpgsign=false"];

    private readonly TempDir _dir = new();
    private readonly string _upstream;
    private readonly string _pin;

    public BenchWorkspaceTests()
    {
        _upstream = Path.Combine(_dir.Path, "upstream");
        Directory.CreateDirectory(_upstream);
        Git(_upstream, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(_upstream, "a.txt"), "one\n");
        Git(_upstream, "add", "-A");
        Git(_upstream, "commit", "-q", "-m", "first");
        _pin = Git(_upstream, "rev-parse", "HEAD").Trim();
        // The upstream moves on: the pin is not its tip, as a real pin rarely is.
        File.WriteAllText(Path.Combine(_upstream, "a.txt"), "one\ntwo\n");
        Git(_upstream, "commit", "-q", "-am", "second");
    }

    public void Dispose() => _dir.Dispose();

    private static string Git(string where, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = where, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in Identity.Concat(args))
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        psi.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
        return output;
    }

    private BenchRepository Repo(string commit = "") => new("lib", "python", _upstream, commit.Length > 0 ? commit : _pin, "MIT", ["true"]);

    private string RemoteUrl => "file://" + Path.Combine(_dir.Path, "remotes", "lib.git");

    private GitBenchWorkspace Workspace(string? acceptanceRoot = null) => new(Path.Combine(_dir.Path, "work"), acceptanceRoot);

    [Fact]
    public async Task The_clone_is_at_the_pin_with_the_remote_and_default_branch_the_server_reads()
    {
        var workspace = Workspace();
        var path = await workspace.PrepareAsync(Repo(), RemoteUrl, default);
        Assert.Equal(workspace.ClonePath(Repo()), path);
        Assert.Equal(_pin, Git(path, "rev-parse", "HEAD").Trim());
        Assert.Equal("main", Git(path, "symbolic-ref", "--short", "HEAD").Trim());
        Assert.Equal(RemoteUrl, Git(path, "config", "--get", "remote.origin.url").Trim());
        Assert.Equal("origin/main", Git(path, "symbolic-ref", "--short", "refs/remotes/origin/HEAD").Trim());
        Assert.Equal("", Git(path, "status", "--porcelain").Trim());
        // The tip of the upstream is not in the clone: the history stops at the pin.
        Assert.Equal("1", Git(path, "rev-list", "--count", "HEAD").Trim());
    }

    [Fact]
    public async Task Preparing_again_keeps_the_clone_and_refuses_one_that_was_touched_or_moved()
    {
        var workspace = Workspace();
        var path = await workspace.PrepareAsync(Repo(), RemoteUrl, default);
        Assert.Equal(path, await workspace.PrepareAsync(Repo(), RemoteUrl + "x", default));
        Assert.Equal(RemoteUrl + "x", Git(path, "config", "--get", "remote.origin.url").Trim());

        File.WriteAllText(Path.Combine(path, "dirty.txt"), "x");
        await Assert.ThrowsAsync<BenchException>(() => workspace.PrepareAsync(Repo(), RemoteUrl, default));
        File.Delete(Path.Combine(path, "dirty.txt"));

        var other = Git(_upstream, "rev-parse", "HEAD").Trim();
        var e = await Assert.ThrowsAsync<BenchException>(() => workspace.PrepareAsync(Repo(other), RemoteUrl, default));
        Assert.Contains("not the clean checkout", e.Message);
        Assert.Equal(_pin, await workspace.ExistingCommitAsync(Repo(), default));
    }

    [Fact]
    public async Task A_commit_the_upstream_does_not_have_is_an_error_and_a_pin_check_says_so()
    {
        var bogus = new string('a', 40);
        var workspace = Workspace();
        await Assert.ThrowsAsync<BenchException>(() => workspace.PrepareAsync(Repo(bogus), RemoteUrl, default));
        Assert.Null(await workspace.CheckPinAsync(Repo(), default));
        Assert.NotNull(await workspace.CheckPinAsync(Repo(bogus), default));
        // The failed attempt left nothing behind, so the right pin works next.
        Assert.Null(await workspace.ExistingCommitAsync(Repo(), default));
        Assert.Equal(_pin, Git(await workspace.PrepareAsync(Repo(), RemoteUrl, default), "rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task A_file_remote_is_made_and_takes_session_branches_only()
    {
        var workspace = Workspace();
        var path = await workspace.PrepareAsync(Repo(), RemoteUrl, default);
        var bare = Path.Combine(_dir.Path, "remotes", "lib.git");
        Assert.Equal(_pin, Git(bare, "rev-parse", "main").Trim());

        Git(path, "checkout", "-q", "-b", "chargehand/run-1");
        File.WriteAllText(Path.Combine(path, "b.txt"), "x");
        Git(path, "add", "-A");
        Git(path, "commit", "-q", "-m", "work");
        Git(path, "push", "-q", "origin", "chargehand/run-1");
        Assert.Equal(Git(path, "rev-parse", "HEAD").Trim(), Git(bare, "rev-parse", "chargehand/run-1").Trim());

        Git(path, "checkout", "-q", "main");
        File.WriteAllText(Path.Combine(path, "c.txt"), "x");
        Git(path, "add", "-A");
        Git(path, "commit", "-q", "-m", "to main");
        var refused = Process.Start(new ProcessStartInfo("git", ["push", "origin", "main"]) { WorkingDirectory = path, RedirectStandardError = true, RedirectStandardOutput = true })!;
        await refused.WaitForExitAsync();
        Assert.NotEqual(0, refused.ExitCode);
        Assert.Equal(_pin, Git(bare, "rev-parse", "main").Trim());
    }

    [Fact]
    public async Task A_pushed_branch_is_fetched_for_the_acceptance_check_and_a_missing_one_is_null()
    {
        var workspace = Workspace();
        var path = await workspace.PrepareAsync(Repo(), RemoteUrl, default);
        var bare = Path.Combine(_dir.Path, "remotes", "lib.git");
        var session = Path.Combine(_dir.Path, "session");
        Git(_dir.Path, "clone", "-q", bare, session);
        Git(session, "checkout", "-q", "-b", "chargehand/run-2");
        File.WriteAllText(Path.Combine(session, "feature.txt"), "x");
        Git(session, "add", "-A");
        Git(session, "commit", "-q", "-m", "feature");
        Git(session, "push", "-q", "origin", "chargehand/run-2");

        Assert.Equal(path, await workspace.FetchBranchAsync(Repo(), "chargehand/run-2", default));
        Assert.Equal(Git(session, "rev-parse", "HEAD").Trim(), Git(path, "rev-parse", "chargehand/run-2").Trim());
        Assert.Null(await workspace.FetchBranchAsync(Repo(), "chargehand/nope", default));
    }

    [Fact]
    public async Task With_an_acceptance_root_the_branch_lands_in_a_copy_there()
    {
        var root = Path.Combine(_dir.Path, "accept-root");
        var workspace = Workspace(root);
        var path = await workspace.PrepareAsync(Repo(), RemoteUrl, default);
        var bare = Path.Combine(_dir.Path, "remotes", "lib.git");
        var session = Path.Combine(_dir.Path, "session");
        Git(_dir.Path, "clone", "-q", bare, session);
        Git(session, "checkout", "-q", "-b", "chargehand/run-3");
        File.WriteAllText(Path.Combine(session, "feature.txt"), "x");
        Git(session, "add", "-A");
        Git(session, "commit", "-q", "-m", "feature");
        Git(session, "push", "-q", "origin", "chargehand/run-3");

        var directory = await workspace.FetchBranchAsync(Repo(), "chargehand/run-3", default);
        Assert.Equal(Path.Combine(root, "lib"), directory);
        Assert.NotEqual(path, directory);
        Assert.Equal(Git(session, "rev-parse", "HEAD").Trim(), Git(directory!, "rev-parse", "chargehand/run-3").Trim());
    }

    [Fact]
    public async Task The_baseline_branch_has_the_pins_tree_and_one_commit_on_top()
    {
        var workspace = Workspace();
        var path = await workspace.PrepareAsync(Repo(), RemoteUrl, default);
        var directory = await workspace.BaselineBranchAsync(Repo(), "chargehand/bench-baseline-lib", default);
        Assert.Equal(path, directory);
        var tip = Git(path, "rev-parse", "chargehand/bench-baseline-lib").Trim();
        Assert.Equal(_pin, Git(path, "rev-parse", tip + "^").Trim());
        Assert.Equal(Git(path, "rev-parse", _pin + "^{tree}").Trim(), Git(path, "rev-parse", tip + "^{tree}").Trim());
    }
}
