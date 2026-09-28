using System.Diagnostics;

namespace Chargehand.Tests;

/// <summary>scripts/prompt-ci.sh against a local "origin" and a stand-in gh. The pull request's head moves after the run
/// was approved; the gate must still evaluate the approved commit.</summary>
public sealed class PromptCiTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _work;
    private readonly string _approved;
    private readonly string _moved;

    public PromptCiTests()
    {
        var origin = Path.Combine(_dir.Path, "origin.git");
        _work = Path.Combine(_dir.Path, "work");
        Directory.CreateDirectory(_work);
        Git(_dir.Path, "init", "-q", "--bare", origin);
        Git(_work, "init", "-q");
        Git(_work, "remote", "add", "origin", origin);
        Commit("README.md", "base\n");
        Git(_work, "push", "-q", "origin", "HEAD:main");
        _approved = Commit("docs/notes.md", "approved\n");
        _moved = Commit("docs/notes.md", "pushed after the approval\n");
        Git(_work, "push", "-q", "origin", $"{_moved}:refs/pull/1/head");
    }

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_gate_evaluates_the_head_it_was_approved_for(bool fromWorkflow)
    {
        var log = Path.Combine(_dir.Path, "gh.log");
        var bin = Path.Combine(_dir.Path, "bin");
        var gh = _dir.Write("bin/gh", $"""
            #!/bin/sh
            case "$1 $2" in
              "repo view") echo o/r ;;
              "pr view") echo {_moved} ;;
              "api -X") printf '%s\n' "$*" >> "{log}" ;;
            esac
            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var psi = new ProcessStartInfo(Repo.Path("scripts", "prompt-ci.sh")) { WorkingDirectory = _work };
        psi.ArgumentList.Add("1");
        psi.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        psi.Environment["CHARGEHAND_PROFILE"] = _dir.Write("eval.json", "{}");
        if (fromWorkflow)
            psi.Environment["HEAD"] = _approved;
        else
            psi.Environment.Remove("HEAD");
        using var p = Process.Start(psi)!;
        p.WaitForExit();

        Assert.Equal(0, p.ExitCode);
        var expected = fromWorkflow ? _approved : _moved; // by hand, the pull request's head now
        Assert.Contains($"repos/o/r/statuses/{expected} ", File.ReadAllText(log));
    }

    private string Commit(string path, string content)
    {
        _dir.Write(Path.Combine("work", path), content);
        Git(_work, "add", path);
        Git(_work, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-qm", path);
        return Git(_work, "rev-parse", "HEAD").Trim();
    }

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}
