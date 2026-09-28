using System.Diagnostics;

namespace Chargehand.Tests;

/// <summary>scripts/check-private-terms.sh, which the pre-commit hook runs over the index and the commit-msg hook over
/// the message, against the gitignored .private-terms denylist.</summary>
public sealed class PrivateTermsTests : IDisposable
{
    private readonly TempDir _repo = new();

    public PrivateTermsTests()
    {
        Git("init", "-q");
        Git("config", "user.name", "Pat Private");
        Git("config", "user.email", "pat@example.invalid");
        _repo.Write(".private-terms", "# one extended regex per line\nprivate\nsecret-host\\.example\n");
        Stage("notes.md", "nothing to see\n");
    }

    public void Dispose() => _repo.Dispose();

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

    private void Stage(string path, string content)
    {
        _repo.Write(path, content);
        Git("add", path);
    }

    private (int Exit, string Stderr) Check(string arg)
    {
        var psi = new ProcessStartInfo(Repo.Path("scripts", "check-private-terms.sh"))
        {
            WorkingDirectory = _repo.Path,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stderr);
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo.Path };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }
}
