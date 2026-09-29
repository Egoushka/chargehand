using System.Diagnostics;

namespace Chargehand.Tests;

/// <summary>scripts/check-private-terms.sh, which the pre-commit hook runs over the index and the commit-msg hook over
/// the message, against the gitignored .private-terms denylist. A linked worktree has no copy of that file, so it reads
/// the main checkout's.</summary>
public sealed class PrivateTermsTests : IDisposable
{
    private const string Term = "zz-private-term-zz";

    private readonly TempDir _repo = new();
    private readonly TempDir _worktrees = new();

    public PrivateTermsTests()
    {
        Git("init", "-q");
        Git("config", "user.name", "Pat Private");
        Git("config", "user.email", "pat@example.invalid");
        _repo.Write(".private-terms", "# one extended regex per line\nprivate\nsecret-host\\.example\n");
        Stage("notes.md", "nothing to see\n");
    }

    public void Dispose()
    {
        _worktrees.Dispose();
        _repo.Dispose();
    }

    [Fact]
    public void Staged_content_without_a_term_passes() => Assert.Equal(0, Check("--cached").Exit);

    [Fact]
    public void A_term_anywhere_in_the_index_blocks()
    {
        Stage("docs/setup.md", "ssh secret-host.example\n");
        var (exit, stderr) = Check("--cached");
        Assert.Equal(1, exit);
        Assert.Contains("docs/setup.md:1:", stderr);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_malformed_pattern_blocks_rather_than_reading_as_no_match(bool staged)
    {
        _repo.Write(".private-terms", "unbalanced(\n");
        var (exit, stderr) = Check(staged ? "--cached" : _repo.Write("MSG", "fix: anything\n"));
        Assert.Equal(1, exit);
        Assert.Contains("could not check", stderr);
    }

    [Fact]
    public void A_term_in_the_commit_message_blocks()
    {
        var (exit, stderr) = Check(_repo.Write("MSG", "fix: reach secret-host.example directly\n"));
        Assert.Equal(1, exit);
        Assert.Contains("secret-host.example", stderr);
    }

    [Fact]
    public void Own_sign_off_and_the_verbose_diff_are_not_the_message()
    {
        var msg = _repo.Write("MSG", """
            fix: drop the old host

            Signed-off-by: Pat Private <pat@example.invalid>
            # ------------------------ >8 ------------------------
            -ssh secret-host.example

            """);
        Assert.Equal(0, Check(msg).Exit);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_linked_worktree_without_its_own_list_uses_the_main_checkouts(bool staged)
    {
        _repo.Write(".private-terms", Term + "\n");
        var worktree = AddWorktree();
        var (exit, stderr) = staged
            ? Check("--cached", Stage(worktree, "docs/setup.md", $"see {Term}\n"))
            : Check(Write(worktree, "MSG", $"fix: mention {Term}\n"), worktree);
        Assert.Equal(1, exit);
        Assert.Contains(Term, stderr);
        Assert.DoesNotContain("is missing", stderr);
    }

    [Fact]
    public void A_linked_worktree_without_its_own_list_passes_clean_content()
    {
        _repo.Write(".private-terms", Term + "\n");
        var worktree = AddWorktree();
        Stage(worktree, "docs/setup.md", "nothing to see\n");
        Assert.Equal(0, Check("--cached", worktree).Exit);
    }

    [Fact]
    public void A_linked_worktrees_own_list_wins_over_the_main_checkouts()
    {
        _repo.Write(".private-terms", Term + "\n");
        var worktree = AddWorktree();
        Write(worktree, ".private-terms", "zz-other-term-zz\n");

        Stage(worktree, "a.md", $"{Term}\n");
        Assert.Equal(0, Check("--cached", worktree).Exit);

        Stage(worktree, "b.md", "zz-other-term-zz\n");
        var (exit, stderr) = Check("--cached", worktree);
        Assert.Equal(1, exit);
        Assert.Contains("b.md:1:", stderr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Without_any_list_the_check_fails_closed_with_the_fix_hint(bool inWorktree)
    {
        File.Delete(Path.Combine(_repo.Path, ".private-terms"));
        var (exit, stderr) = Check("--cached", inWorktree ? AddWorktree() : _repo.Path);
        Assert.Equal(1, exit);
        Assert.Contains(".private-terms is missing", stderr);
        Assert.Contains(".private-terms.example", stderr);
    }

    // A worktree needs a commit to branch from; detached, so no branch name is taken.
    private string AddWorktree()
    {
        GitIn(_repo.Path, "-c", "core.hooksPath=/dev/null", "commit", "-q", "-m", "init");
        var worktree = Path.Combine(_worktrees.Path, "linked");
        GitIn(_repo.Path, "worktree", "add", "-q", "--detach", worktree);
        return worktree;
    }

    private void Stage(string path, string content) => Stage(_repo.Path, path, content);

    private static string Stage(string dir, string path, string content)
    {
        Write(dir, path, content);
        GitIn(dir, "add", path);
        return dir;
    }

    private static string Write(string dir, string path, string content)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private (int Exit, string Stderr) Check(string arg) => Check(arg, _repo.Path);

    private static (int Exit, string Stderr) Check(string arg, string dir)
    {
        var psi = new ProcessStartInfo(Repo.Path("scripts", "check-private-terms.sh"))
        {
            WorkingDirectory = dir,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stderr);
    }

    private void Git(params string[] args) => GitIn(_repo.Path, args);

    // A setup step that fails must fail the test, not leave a check running against a half-built repo.
    private static void GitIn(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} exited {p.ExitCode}");
    }
}
