using System.Diagnostics;
using System.Text;

namespace Chargehand.Kernel;

/// <summary>What one git run printed. <see cref="TimedOut"/> is set when the deadline killed it; the exit code is then -1.</summary>
public sealed record GitRunResult(int ExitCode, string Stdout, string Stderr, bool TimedOut = false);

/// <summary>Runs git with arguments (never a shell), stdin closed, stdout and stderr drained concurrently. A git that cannot be started throws
/// <see cref="System.ComponentModel.Win32Exception"/>; cancelling <c>ct</c> kills the process tree and throws <see cref="OperationCanceledException"/>.</summary>
public static class GitRunner
{
    /// <param name="workingDirectory">The process's directory; null keeps the caller's (for callers that pass <c>-C</c> themselves).</param>
    /// <param name="args">Arguments, passed one by one.</param>
    /// <param name="ct">Cancels the run and kills git.</param>
    /// <param name="timeout">A deadline after which git is killed and the result reports <see cref="GitRunResult.TimedOut"/>; null means none.</param>
    /// <param name="utf8Stdout">Decode stdout as UTF-8 regardless of the console's encoding.</param>
    public static async Task<GitRunResult> RunAsync(string? workingDirectory, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null, bool utf8Stdout = false)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        if (utf8Stdout)
            psi.StandardOutputEncoding = Encoding.UTF8;
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start git");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } limit)
            deadline.CancelAfter(limit);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (ct.IsCancellationRequested)
                throw;
            return new GitRunResult(-1, "", "git timed out", TimedOut: true);
        }
        return new GitRunResult(process.ExitCode, await stdout, await stderr);
    }
}
