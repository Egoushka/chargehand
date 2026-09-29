using System.Diagnostics;
using Chargehand.Workspace;

namespace Chargehand.Tests;

/// <summary>Goal 0.7 (ADR 0035): the per-run clone and branch a writing node works in.</summary>
public class RunWorkspaceTests
{
    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return output.Trim();
    }

    private static async Task<(TempDir Root, Chargehand.Contracts.RepositoryRef Source, RunWorkspace Workspace)> Make()
    {
        var root = new TempDir();
        var source = Runs.GitRepo(root.Path);
        // The orchestrator passes the cached checkout; the source works as one for these tests.
        var ws = await RunWorkspace.CreateAsync(source.Path, source.Commit, Path.Combine(root.Path, "work"), "run-1", "n1", default);
        return (root, source, ws);
    }

    [Fact]
    public async Task A_workspace_is_a_clone_on_its_own_branch_and_leaves_the_source_alone()
    {
        var (root, source, ws) = await Make();
        using var _ = root;
        Assert.Equal(Path.Combine(root.Path, "work", ".runs", "run-1", "n1"), ws.Directory);
        Assert.Equal("chargehand/run-1/n1", ws.Branch);
        Assert.Equal("chargehand/run-1/n1", Git(ws.Directory, "branch", "--show-current"));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(ws.Directory, "README.md")).Trim());
        Assert.Equal(source.Commit, ws.Base);
        Assert.Empty(Git(source.Path, "status", "--porcelain"));
        Assert.DoesNotContain("chargehand/", Git(source.Path, "branch", "--list"), StringComparison.Ordinal);
        Assert.Single(Git(source.Path, "worktree", "list").Split('\n'));
    }

    [Fact]
    public async Task Committing_an_edit_gives_a_branch_the_source_does_not_have()
    {
        var (root, source, ws) = await Make();
        using var _ = root;
        File.WriteAllText(Path.Combine(ws.Directory, "README.md"), "hello world\n");
        File.WriteAllText(Path.Combine(ws.Directory, "new.txt"), "new\n");
        var info = await ws.CommitAsync("feat: greet the world", default);
        Assert.NotNull(info);
        Assert.NotEqual(ws.Base, info.Commit);
        Assert.Equal(info.Commit, Git(ws.Directory, "rev-parse", "chargehand/run-1/n1"));
        Assert.Equal(ws.Directory, info.Repository);
        Assert.Equal(source.Commit, Git(source.Path, "rev-parse", "HEAD"));
        Assert.Equal(["README.md", "new.txt"], await ws.ChangedPathsAsync(default));
        Assert.Contains("+hello world", await ws.DiffAsync(default), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commit_of_an_unchanged_tree_is_null()
    {
        var (root, _, ws) = await Make();
        using var _r = root;
        Assert.Null(await ws.CommitAsync("feat: nothing", default));
        Assert.Equal(ws.Base, Git(ws.Directory, "rev-parse", "HEAD"));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Commit_runs_no_repository_hook()
    {
        var (root, _, ws) = await Make();
        using var _r = root;
        var hook = Path.Combine(ws.Directory, ".git", "hooks", "pre-commit");
        File.WriteAllText(hook, "#!/bin/sh\nexit 1\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(ws.Directory, "a.txt"), "a\n");
        Assert.NotNull(await ws.CommitAsync("feat: a", default));
    }

    [Fact]
    public async Task Diff_is_truncated_at_64_KiB_with_a_marker()
    {
        var (root, _, ws) = await Make();
        using var _r = root;
        File.WriteAllText(Path.Combine(ws.Directory, "big.txt"), string.Join("\n", Enumerable.Range(0, 20000).Select(i => $"line {i} of the big file")));
        await ws.CommitAsync("feat: big", default);
        var diff = await ws.DiffAsync(default);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(diff) <= 65536);
        Assert.EndsWith("[diff truncated at 65536 bytes]", diff, StringComparison.Ordinal);
    }
}
