using System.Diagnostics;
using System.Text;

namespace Chargehand.Modules.Runners.Api;

/// <summary>The result of one <see cref="GitProcess.RunAsync"/>: what git printed, kept to a prefix of <c>maxBytes</c>.</summary>
public sealed record GitResult(int ExitCode, string Stdout, string Stderr, bool Truncated)
{
    public string Tail => ((Stderr.Length > 0 ? Stderr : Stdout).Trim() is var t && t.Length > 400 ? t[^400..] : t);
}

/// <summary>git on the server's machine, in a clean environment and with a deadline: for the handover's scratch clone and the bundles a runner on another machine clones from.</summary>
public static class GitProcess
{
    /// <summary>A clean environment: no user git configuration, no prompts, no askpass, no inherited credentials; PATH only, so git and ssh are found.</summary>
    public static Dictionary<string, string> Environment(string home) => new(StringComparer.Ordinal)
    {
        ["PATH"] = System.Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
        ["HOME"] = home,
        ["GIT_CONFIG_GLOBAL"] = "/dev/null",
        ["GIT_CONFIG_SYSTEM"] = "/dev/null",
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_ASKPASS"] = "/bin/true",
        ["LC_ALL"] = "C",
    };

    public static async Task<GitResult> RunAsync(string directory, IReadOnlyDictionary<string, string> env, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, int maxBytes = 1024 * 1024)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        // fetch and merge would start a detached `git maintenance` that repacks after this call has returned, racing with the next command and with the removal of the scratch clone.
        foreach (var a in (string[])["-c", "maintenance.auto=false", "-c", "gc.auto=0", .. args])
            psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var (k, v) in env)
            psi.Environment[k] = v;
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start git");
        process.StandardInput.Close();
        var truncated = false;
        async Task<string> Read(StreamReader r)
        {
            var text = new StringBuilder();
            var buffer = new char[8192];
            int n;
            while ((n = await r.ReadAsync(buffer)) > 0)
            {
                // Keep a prefix: what fits of this read, then nothing more, so a cut output is never spliced.
                var take = truncated ? 0 : Math.Min(n, maxBytes - text.Length);
                text.Append(buffer, 0, take);
                truncated |= take < n;
            }
            return text.ToString();
        }
        var stdout = Read(process.StandardOutput);
        var stderr = Read(process.StandardError);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (ct.IsCancellationRequested)
                throw;
            return new GitResult(-1, "", "git timed out", false);
        }
        return new GitResult(process.ExitCode, await stdout, await stderr, truncated);
    }
}
