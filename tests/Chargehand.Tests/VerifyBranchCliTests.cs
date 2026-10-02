using System.Diagnostics;
using System.Text.Json;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary><c>chargehand verify-branch</c>, the command a fresh verification container runs (ADR 0039): put the session's bundle on a clean workspace, run the tests, print one line.</summary>
public class VerifyBranchCliTests
{
    private static string G(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {output}");
        return output;
    }

    /// <summary>A workspace at the base commit (what the workspace helper leaves) and a bundle of a branch that adds retry.txt.</summary>
    private sealed class Fixture : IDisposable
    {
        public TempDir Dir { get; } = new();
        public string Work => Path.Combine(Dir.Path, "work");
        public string Bundle => Path.Combine(Dir.Path, "chargehand.bundle");

        public Fixture(bool bundleBranch = true)
        {
            var source = Directory.CreateDirectory(Path.Combine(Dir.Path, "source")).FullName;
            G(source, "init", "-q", "-b", "main");
            File.WriteAllText(Path.Combine(source, "a.txt"), "a");
            G(source, "add", "-A");
            G(source, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "-m", "base");
            G(Dir.Path, "clone", "-q", "--template=", source, Work);
            G(Work, "checkout", "-q", "-b", "chargehand/run1");                       // the workspace helper's branch, at the base
            var session = Path.Combine(Dir.Path, "session");
            G(Dir.Path, "clone", "-q", "--template=", source, session);
            G(session, "checkout", "-q", "-b", "chargehand/run1");
            File.WriteAllText(Path.Combine(session, "retry.txt"), "retry");
            G(session, "add", "-A");
            G(session, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "-m", "feat: retry");
            if (bundleBranch)
                G(session, "bundle", "create", Bundle, "chargehand/run1", "--not", "--remotes");
        }

        public void Dispose() => Dir.Dispose();
    }

    private static async Task<(int Exit, JsonElement Record)> Run(Fixture f, string timeout, params string[] argv)
    {
        var output = new StringWriter();
        var exit = await VerifyBranchCli.RunAsync(["--work", f.Work, "--bundle", f.Bundle, "--branch", "chargehand/run1", "--timeout-seconds", timeout, "--", .. argv], output, TextWriter.Null, default);
        var line = output.ToString().Split('\n').Last(l => l.StartsWith(VerifyBranchCli.Marker, StringComparison.Ordinal));
        return (exit, JsonDocument.Parse(line[VerifyBranchCli.Marker.Length..]).RootElement);
    }

    [Fact]
    public async Task The_command_runs_on_the_bundles_branch_and_a_pass_is_exit_code_0()
    {
        using var f = new Fixture();
        var (exit, record) = await Run(f, "30", "sh", "-c", "test -f retry.txt && echo saw-the-change");
        Assert.Equal(0, exit);
        Assert.Equal(0, record.GetProperty("exit_code").GetInt32());
        Assert.False(record.GetProperty("timed_out").GetBoolean());
        Assert.Contains("saw-the-change", record.GetProperty("output_tail").GetString());
        Assert.Equal(["sh", "-c", "test -f retry.txt && echo saw-the-change"], record.GetProperty("argv").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(G(f.Work, "rev-parse", "HEAD").Trim(), record.GetProperty("commit").GetString());
    }

    [Fact]
    public async Task A_failing_command_is_its_own_exit_code_in_the_record_and_verify_branch_still_exits_0()
    {
        using var f = new Fixture();
        var (exit, record) = await Run(f, "30", "sh", "-c", "echo boom >&2; exit 3");
        Assert.Equal(0, exit);                                            // it ran; the record carries the verdict
        Assert.Equal(3, record.GetProperty("exit_code").GetInt32());
        Assert.Contains("boom", record.GetProperty("output_tail").GetString());
    }

    [Fact]
    public async Task A_command_that_outruns_the_timeout_is_killed_and_marked()
    {
        using var f = new Fixture();
        var sw = Stopwatch.StartNew();
        var (_, record) = await Run(f, "1", "sh", "-c", "sleep 60");
        Assert.True(record.GetProperty("timed_out").GetBoolean());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Output_is_cut_to_its_tail()
    {
        using var f = new Fixture();
        var (_, record) = await Run(f, "30", "sh", "-c", "i=0; while [ $i -lt 3000 ]; do echo line-$i-padding-padding-padding; i=$((i+1)); done");
        var tail = record.GetProperty("output_tail").GetString()!;
        Assert.True(tail.Length <= 8192 + 64);
        Assert.Contains("line-2999-", tail);
        Assert.DoesNotContain("line-0-", tail);
    }

    [Fact]
    public async Task A_bundle_that_does_not_apply_is_exit_2_with_a_record_saying_so()
    {
        using var f = new Fixture(bundleBranch: false);
        var output = new StringWriter();
        var exit = await VerifyBranchCli.RunAsync(["--work", f.Work, "--bundle", f.Bundle, "--branch", "chargehand/run1", "--timeout-seconds", "30", "--", "true"], output, TextWriter.Null, default);
        Assert.Equal(2, exit);
        var line = output.ToString().Split('\n').Last(l => l.StartsWith(VerifyBranchCli.Marker, StringComparison.Ordinal));
        var record = JsonDocument.Parse(line[VerifyBranchCli.Marker.Length..]).RootElement;
        Assert.Equal(-1, record.GetProperty("exit_code").GetInt32());
        Assert.Contains("bundle", record.GetProperty("output_tail").GetString());
    }

    [Theory]
    [InlineData]
    [InlineData("--bundle", "b")]
    [InlineData("--bundle", "b", "--branch", "main", "--timeout-seconds", "5", "--", "true")]      // not under chargehand/
    [InlineData("--bundle", "b", "--branch", "chargehand/r", "--timeout-seconds", "0", "--", "true")]
    [InlineData("--bundle", "b", "--branch", "chargehand/r", "--timeout-seconds", "5")]              // no command
    public async Task A_bad_command_line_prints_usage_and_exits_2(params string[] args)
    {
        var error = new StringWriter();
        Assert.Equal(2, await VerifyBranchCli.RunAsync(args, TextWriter.Null, error, default));
        Assert.Contains("usage: chargehand verify-branch", error.ToString());
    }
}
