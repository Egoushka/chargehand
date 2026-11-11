using System.Diagnostics;
using Chargehand.Automation;

namespace Chargehand.Tests;

/// <summary>chargehand claims check: exit codes, one line per finding, and the changed-since filter over a real temporary git repository.</summary>
public class ClaimsCliTests
{
    private static async Task<(int Code, string Output, string Error)> Run(string root, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await ClaimsCli.RunAsync(["check", "--root", root, .. args], root, output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        string[] identity = ["-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false"];
        foreach (var a in identity.Concat(args))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, text);
    }

    [Fact]
    public async Task Check_exits_1_with_one_line_per_finding_and_0_when_clean()
    {
        using var dir = new TempDir();
        dir.Write("src/A.cs", "class Alpha {}\n");
        dir.Write("docs/guide/x.md", "ok <!-- cites: src/A.cs#Alpha -->\n");
        var clean = await Run(dir.Path);
        Assert.Equal((0, ""), (clean.Code, clean.Output));

        dir.Write("docs/guide/y.md", "\nstale <!-- cites: src/A.cs#Beta -->\ngone <!-- cites: src/B.cs -->\n");
        var stale = await Run(dir.Path);
        Assert.Equal(1, stale.Code);
        Assert.Equal(["docs/guide/y.md:2: symbol-missing src/A.cs#Beta", "docs/guide/y.md:3: file-missing src/B.cs"], stale.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Json_lists_the_findings()
    {
        using var dir = new TempDir();
        dir.Write("docs/guide/x.md", "gone <!-- cites: src/B.cs -->\n");
        var result = await Run(dir.Path, "--json");
        Assert.Equal(1, result.Code);
        Assert.Contains("\"reason\":\"file-missing\"", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Usage_error_exits_2()
    {
        using var dir = new TempDir();
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(2, await ClaimsCli.RunAsync(["nope"], dir.Path, output, error, CancellationToken.None));
        Assert.Contains("usage", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changed_since_checks_only_claims_whose_file_changed()
    {
        using var dir = new TempDir();
        dir.Write("src/A.cs", "class Alpha {}\n");
        dir.Write("src/B.cs", "class Bravo {}\n");
        dir.Write("docs/guide/x.md", "a <!-- cites: src/A.cs#Alpha -->\nb <!-- cites: src/B.cs#Bravo -->\n");
        Git(dir.Path, "init", "-q");
        Git(dir.Path, "add", ".");
        Git(dir.Path, "commit", "-q", "-m", "one");
        dir.Write("src/A.cs", "class Renamed {}\n");
        dir.Write("src/B.cs", "class Changed {}\n");
        Git(dir.Path, "add", ".");
        Git(dir.Path, "commit", "-q", "-m", "two");
        File.Delete(Path.Combine(dir.Path, "src/B.cs"));
        dir.Write("src/B.cs", "class Bravo {}\n");
        Git(dir.Path, "add", ".");
        Git(dir.Path, "commit", "-q", "-m", "three");

        var all = await Run(dir.Path);
        Assert.Equal(["docs/guide/x.md:1: symbol-missing src/A.cs#Alpha"], all.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        var sinceTwo = await Run(dir.Path, "--changed-since", "HEAD~1");
        Assert.Equal(0, sinceTwo.Code);
        var sinceOne = await Run(dir.Path, "--changed-since", "HEAD~2");
        Assert.Equal(["docs/guide/x.md:1: symbol-missing src/A.cs#Alpha"], sinceOne.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(2, (await Run(dir.Path, "--changed-since", "no-such-ref")).Code);
    }
}
