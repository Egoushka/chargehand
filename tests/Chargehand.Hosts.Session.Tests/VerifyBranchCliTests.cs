using System.Diagnostics;
using System.Text.Json;
using Chargehand.Hosts.Session;
using Chargehand.Modules.Handover.Api;

namespace Chargehand.Hosts.Session.Tests;

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

        /// <summary>A bundle of a branch off the same base that adds blind.txt, as a blind task's tests-only branch does; the argument for <c>--tests-bundle</c>.</summary>
        public string BlindBundle(string name = "blind1")
        {
            var clone = Path.Combine(Dir.Path, name);
            G(Dir.Path, "clone", "-q", "--template=", Path.Combine(Dir.Path, "source"), clone);
            G(clone, "checkout", "-q", "-b", $"chargehand/{name}");
            File.WriteAllText(Path.Combine(clone, "blind.txt"), "tests");
            G(clone, "add", "-A");
            G(clone, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "-m", "test: blind");
            var bundle = Path.Combine(Dir.Path, $"{name}.bundle");
            G(clone, "bundle", "create", bundle, $"chargehand/{name}", "--not", "--remotes");
            return $"{bundle}@chargehand/{name}";
        }

        public void Dispose() => Dir.Dispose();
    }

    private static async Task<(int Exit, JsonElement Record)> RunBlind(Fixture f, string[] testsBundles, string script)
    {
        var output = new StringWriter();
        List<string> args = ["--work", f.Work, "--bundle", f.Bundle, "--branch", "chargehand/run1", "--timeout-seconds", "30"];
        foreach (var b in testsBundles)
            args.AddRange(["--tests-bundle", b]);
        args.AddRange(["--", "sh", "-c", script]);
        var exit = await VerifyBranchCli.RunAsync(args, output, TextWriter.Null, default);
        var line = output.ToString().Split('\n').Last(l => l.StartsWith(VerifyBranchCli.Marker, StringComparison.Ordinal));
        return (exit, JsonDocument.Parse(line[VerifyBranchCli.Marker.Length..]).RootElement);
    }

    private const string FailsWithBlindFile = "if [ -f blind.txt ]; then echo '  Failed Boundary_Test [3 ms]'; echo 'Failed!  - Failed:     1, Passed:     4, Skipped:     0, Total:     5'; exit 1; fi; echo 'Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3'";

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
    public async Task The_command_sees_the_base_commit_it_started_from_in_its_environment()
    {
        using var f = new Fixture();
        var baseCommit = G(f.Work, "rev-parse", "HEAD").Trim();
        var (_, record) = await Run(f, "30", "sh", "-c", "echo base=$CHARGEHAND_BASE_COMMIT checks=$AGENT_CHECKS_BASE");
        var tail = record.GetProperty("output_tail").GetString();
        Assert.Contains($"base={baseCommit} checks={baseCommit}", tail);
        Assert.NotEqual(baseCommit, record.GetProperty("commit").GetString());   // the branch moved HEAD on; the variables name the commit before it
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
    public async Task A_program_that_is_not_in_the_image_is_exit_code_127_not_a_crash()
    {
        using var f = new Fixture();
        var (exit, record) = await Run(f, "30", "chargehand-no-such-tool-xyz", "test");
        Assert.Equal(0, exit);
        Assert.Equal(127, record.GetProperty("exit_code").GetInt32());
        Assert.Contains("chargehand-no-such-tool-xyz: command not found", record.GetProperty("output_tail").GetString());
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

    [Fact]
    public async Task A_failing_blind_test_is_in_the_blind_part_of_the_record_while_the_main_run_still_passes()
    {
        using var f = new Fixture();
        var (exit, record) = await RunBlind(f, [f.BlindBundle()], FailsWithBlindFile);
        Assert.Equal(0, exit);
        Assert.Equal(0, record.GetProperty("exit_code").GetInt32());          // the repository's own tests, without the blind overlay
        var blind = record.GetProperty("blind");
        Assert.Equal(1, blind.GetProperty("failed").GetInt32());
        Assert.Equal(5, blind.GetProperty("count").GetInt32());
        Assert.Equal(["Boundary_Test"], blind.GetProperty("names").EnumerateArray().Select(n => n.GetString()));
        Assert.Empty(blind.GetProperty("uncompilable").EnumerateArray());
    }

    [Fact]
    public async Task Passing_blind_tests_are_counted_with_nothing_failed()
    {
        using var f = new Fixture();
        var (_, record) = await RunBlind(f, [f.BlindBundle()], "echo 'Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3'");
        var blind = record.GetProperty("blind");
        Assert.Equal(0, blind.GetProperty("failed").GetInt32());
        Assert.Equal(3, blind.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Blind_tests_that_do_not_compile_on_the_base_commit_are_dropped_and_do_not_fail_the_run()
    {
        using var f = new Fixture();
        var (_, record) = await RunBlind(f, [f.BlindBundle()], "if [ -f blind.txt ]; then echo 'blind.txt(1,1): error CS1002: ; expected'; exit 1; fi; echo ok");
        Assert.Equal(0, record.GetProperty("exit_code").GetInt32());
        var blind = record.GetProperty("blind");
        Assert.Equal(0, blind.GetProperty("failed").GetInt32());
        Assert.Equal(["chargehand/blind1"], blind.GetProperty("uncompilable").EnumerateArray().Select(n => n.GetString()));
    }

    [Fact]
    public async Task Without_a_tests_bundle_the_record_has_no_blind_part()
    {
        using var f = new Fixture();
        var (_, record) = await Run(f, "30", "true");
        Assert.True(!record.TryGetProperty("blind", out var blind) || blind.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task A_tests_bundle_with_no_branch_prints_usage_and_exits_2()
    {
        var error = new StringWriter();
        Assert.Equal(2, await VerifyBranchCli.RunAsync(["--bundle", "b", "--branch", "chargehand/r", "--timeout-seconds", "5", "--tests-bundle", "nobranch", "--", "true"], TextWriter.Null, error, default));
        Assert.Contains("--tests-bundle", error.ToString());
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

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("chargehand-test-").FullName;

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
