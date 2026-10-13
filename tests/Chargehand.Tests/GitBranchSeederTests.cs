using System.Diagnostics;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>ADR 0051 with real git: the pushed branch of a finished task is fetched from the remote into a scratch clone and bundled against the base commit, the way a session's own bundle is.</summary>
public sealed class GitBranchSeederTests : IDisposable
{
    private static readonly string[] Identity = ["-c", "user.name=t", "-c", "user.email=t@example.test", "-c", "commit.gpgsign=false"];

    private readonly TempDir _dir = new();
    private readonly string _source;
    private readonly string _remote;
    private readonly string _commit;
    private const string Branch = "chargehand/run-x";

    public GitBranchSeederTests()
    {
        _source = Path.Combine(_dir.Path, "src");
        _remote = Path.Combine(_dir.Path, "remote.git");
        Directory.CreateDirectory(_source);
        Git(_source, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(_source, "a.txt"), "one\n");
        Git(_source, "add", "-A");
        Git(_source, "commit", "-q", "-m", "base");
        _commit = Git(_source, "rev-parse", "HEAD").Trim();
        Git(_dir.Path, "init", "-q", "--bare", "-b", "main", _remote);
        Git(_source, "push", "-q", _remote, "main");
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

    /// <summary>Pushes commits to the remote's branch from a clone of the base, as the handover (and then a person or the CI fixer) would.</summary>
    private void Push(string branch, string file, string content, bool fromBase = true)
    {
        var work = Path.Combine(_dir.Path, $"work-{Guid.NewGuid():N}");
        Git(_dir.Path, "clone", "-q", _remote, work);
        Git(work, "checkout", "-q", "-B", branch, fromBase ? "origin/main" : $"origin/{branch}");
        File.WriteAllText(Path.Combine(work, file), content);
        Git(work, "add", "-A");
        Git(work, "commit", "-q", "-m", $"change {file}");
        Git(work, "push", "-q", "origin", $"{branch}:{branch}");
    }

    private Task<BranchSeedResult> Build(string branch = Branch, string? remote = null, bool allowLocal = true, string? commit = null) =>
        new GitBranchSeeder().BuildAsync(new BranchSeedRequest(branch, _source, commit ?? _commit, remote ?? $"file://{_remote}", null, Path.Combine(_dir.Path, "scratch"),
            Path.Combine(_dir.Path, "scratch", "run-new-branch"), Path.Combine(_dir.Path, "seed.bundle"), allowLocal), default);

    [Fact]
    public async Task The_branch_as_the_remote_has_it_becomes_a_bundle_a_workspace_at_the_base_can_fetch()
    {
        Push(Branch, "b.txt", "first\n");
        Push(Branch, "c.txt", "second\n", fromBase: false);

        var made = await Build();

        Assert.True(made.Made, made.Failure);
        Assert.Equal(2, made.Commits);
        var workspace = Path.Combine(_dir.Path, "workspace");
        Git(_dir.Path, "clone", "-q", _source, workspace);
        Git(workspace, "fetch", "-q", Path.Combine(_dir.Path, "seed.bundle"), $"refs/heads/{Branch}");
        Assert.Equal("second\n", Git(workspace, "show", "FETCH_HEAD:c.txt"));
        Assert.Equal("first\n", Git(workspace, "show", "FETCH_HEAD:b.txt"));
    }

    [Fact]
    public async Task What_a_person_pushed_to_the_branch_since_is_in_the_seed()
    {
        Push(Branch, "b.txt", "first\n");
        var before = await Build();
        Directory.Delete(Path.Combine(_dir.Path, "scratch", "run-new-branch"), recursive: true);
        Push(Branch, "review.txt", "a reviewer's fix\n", fromBase: false);
        var after = await Build();

        Assert.Equal((1, 2), (before.Commits, after.Commits));
    }

    [Fact]
    public async Task A_branch_the_remote_does_not_have_is_a_failure_with_the_reason()
    {
        var made = await Build();

        Assert.False(made.Made);
        Assert.Contains("the remote has no usable chargehand/run-x", made.Failure);
        Assert.False(File.Exists(Path.Combine(_dir.Path, "seed.bundle")));
    }

    [Fact]
    public async Task A_branch_rewritten_to_start_elsewhere_is_refused()
    {
        Push(Branch, "b.txt", "first\n");
        var elsewhere = Path.Combine(_dir.Path, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Git(elsewhere, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(elsewhere, "z.txt"), "z\n");
        Git(elsewhere, "add", "-A");
        Git(elsewhere, "commit", "-q", "-m", "unrelated");
        Git(elsewhere, "push", "-q", "--force", _remote, $"main:{Branch}");

        var made = await Build();

        Assert.False(made.Made);
        Assert.Equal("the branch on the remote no longer descends from the commit the task started at", made.Failure);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("chargehand/../main")]
    [InlineData("chargehand/x;y")]
    [InlineData("-chargehand/x")]
    public async Task Only_a_chargehand_branch_is_fetched(string branch)
    {
        var made = await Build(branch);

        Assert.False(made.Made);
        Assert.Equal("the branch is not a chargehand branch", made.Failure);
    }

    [Fact]
    public async Task A_local_remote_needs_the_test_switch_and_a_missing_base_commit_is_said()
    {
        Push(Branch, "b.txt", "first\n");

        Assert.Equal("the remote is not an https or ssh remote", (await Build(allowLocal: false)).Failure);
        Assert.Contains("the base commit is not in chargehand's checkout", (await Build(commit: "fffffff0000000fffffff0000000fffffff00000")).Failure);
    }
}
