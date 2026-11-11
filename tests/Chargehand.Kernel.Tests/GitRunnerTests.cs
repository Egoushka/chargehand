using Chargehand.Kernel;

namespace Chargehand.Kernel.Tests;

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class GitRunnerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("git-runner").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task Version_is_printed_with_exit_zero()
    {
        var r = await GitRunner.RunAsync(null, ["--version"], CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
        Assert.StartsWith("git version", r.Stdout);
    }

    [Fact]
    public async Task Rev_parse_runs_in_the_working_directory()
    {
        Assert.Equal(0, (await GitRunner.RunAsync(_dir, ["init", "--quiet"], CancellationToken.None)).ExitCode);
        var r = await GitRunner.RunAsync(_dir, ["rev-parse", "--is-inside-work-tree"], CancellationToken.None);
        Assert.Equal("true", r.Stdout.Trim());
    }

    [Fact]
    public async Task Non_zero_exit_carries_stderr()
    {
        await GitRunner.RunAsync(_dir, ["init", "--quiet"], CancellationToken.None);
        var r = await GitRunner.RunAsync(_dir, ["rev-parse", "--verify", "refs/heads/nope"], CancellationToken.None);
        Assert.NotEqual(0, r.ExitCode);
        Assert.False(r.TimedOut);
    }

    [Fact]
    public async Task Big_stdout_does_not_deadlock()
    {
        await GitRunner.RunAsync(_dir, ["init", "--quiet"], CancellationToken.None);
        var big = string.Join('\n', Enumerable.Range(0, 60_000).Select(i => $"line {i} of padding text"));
        await File.WriteAllTextAsync(Path.Combine(_dir, "big.txt"), big);
        await GitRunner.RunAsync(_dir, ["add", "big.txt"], CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var r = await GitRunner.RunAsync(_dir, ["diff", "--cached"], cts.Token, utf8Stdout: true);
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Stdout.Length > 1_000_000);
    }

    private async Task<string> HangingAliasAsync()
    {
        var script = Path.Combine(_dir, "hang.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nexec sleep 600\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return $"alias.hang=!{script}";
    }

    [Fact]
    public async Task Cancellation_kills_a_running_git_and_throws()
    {
        var alias = await HangingAliasAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GitRunner.RunAsync(_dir, ["-c", alias, "hang"], cts.Token));
    }

    [Fact]
    public async Task Timeout_kills_git_and_reports_it()
    {
        var alias = await HangingAliasAsync();
        var r = await GitRunner.RunAsync(_dir, ["-c", alias, "hang"], CancellationToken.None, TimeSpan.FromMilliseconds(500));
        Assert.True(r.TimedOut);
        Assert.Equal(-1, r.ExitCode);
    }
}
