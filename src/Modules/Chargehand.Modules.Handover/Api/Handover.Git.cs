using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Chargehand.Modules.Handover.Api;

/// <summary>git in the scratch clone: a clean environment, the push's credential, and the remotes chargehand pushes to.</summary>
public sealed partial class Handover
{
    /// <summary>The whole <c>git push</c>: one ref, never forced (no <c>+</c>), never a mirror or a delete. The credential is not here.</summary>
    public static IReadOnlyList<string> PushArgs(string remoteUrl, string branch) =>
        ["push", "--porcelain", remoteUrl, $"refs/heads/{branch}:refs/heads/{branch}"];

    /// <summary>The environment of a push: nothing is inherited from the user's git configuration or askpass, and the credential is an HTTP header through git's
    /// environment configuration (or an ssh key file), so it appears on no command line.</summary>
    public static IReadOnlyDictionary<string, string> PushEnvironment(PushCredential? credential, string remoteUrl, string scratchRoot, IReadOnlyDictionary<string, string>? baseEnvironment = null)
    {
        var env = new Dictionary<string, string>(baseEnvironment ?? GitEnvironment(scratchRoot), StringComparer.Ordinal);
        if (credential is null)
            return env;
        if (credential.Kind == PushCredentialKind.HttpsToken)
        {
            env["GIT_CONFIG_COUNT"] = "1";
            env["GIT_CONFIG_KEY_0"] = "http.extraheader";
            env["GIT_CONFIG_VALUE_0"] = $"Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{credential.Value}"))}";
        }
        else
        {
            var keyFile = Path.Combine(scratchRoot, $"push-key-{Guid.NewGuid():N}");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var writer = new StreamWriter(new FileStream(keyFile, options)))
                writer.Write(credential.Value.TrimEnd() + "\n");
            env["GIT_SSH_COMMAND"] = $"ssh -i {keyFile} -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes -o BatchMode=yes";
        }
        return env;
    }

    /// <summary>A clean environment: no user git configuration, no prompts, no askpass, no inherited credentials; PATH only, so git and ssh are found.</summary>
    internal static Dictionary<string, string> GitEnvironment(string home) => new(StringComparer.Ordinal)
    {
        ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
        ["HOME"] = home,
        ["GIT_CONFIG_GLOBAL"] = "/dev/null",
        ["GIT_CONFIG_SYSTEM"] = "/dev/null",
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_ASKPASS"] = "/bin/true",
        ["LC_ALL"] = "C",
    };

    internal static bool RemoteAllowed(string url, bool allowLocal) =>
        url.Length > 0 && !url.StartsWith('-')
        && (url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("ssh://", StringComparison.Ordinal) || ScpForm().IsMatch(url)
            || (allowLocal && url.StartsWith("file://", StringComparison.Ordinal)));

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+@[A-Za-z0-9_.-]+:[A-Za-z0-9_./-]+$")]
    private static partial Regex ScpForm();

    internal sealed record GitResult(int ExitCode, string Stdout, string Stderr, bool Truncated)
    {
        public string Tail => ((Stderr.Length > 0 ? Stderr : Stdout).Trim() is var t && t.Length > 400 ? t[^400..] : t);
    }

    internal static class Git
    {
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
}
