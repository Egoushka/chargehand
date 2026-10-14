using System.Diagnostics;

namespace Chargehand.Tests;

/// <summary>Real git for a test, with no global or system configuration and a fixed identity; fails the test when git does.</summary>
internal static class TestGit
{
    private static readonly string[] Identity = ["-c", "user.name=t", "-c", "user.email=t@example.test", "-c", "commit.gpgsign=false"];

    internal static string Run(string where, params string[] args)
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
}
