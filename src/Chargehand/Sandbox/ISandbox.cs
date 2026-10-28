namespace Chargehand.Sandbox;

/// <param name="Argv">The command and its arguments; no shell is involved unless the argv names one.</param>
/// <param name="WorkDirectory">The only directory the command may write to, besides a private temp directory.</param>
/// <param name="Network">False by default (ADR 0035): a build that needs the network fails until the profile allows it.</param>
/// <param name="PassEnv">Names of environment variables copied in on top of the fixed allowlist.</param>
/// <param name="Env">Values the caller resolved, set on top of the allowlist (a model credential). Used by <see cref="ISandbox.Wrap"/>.</param>
public sealed record SandboxSpec(IReadOnlyList<string> Argv, string WorkDirectory, TimeSpan Timeout, bool Network, IReadOnlyList<string> PassEnv,
    IReadOnlyDictionary<string, string>? Env = null);

/// <param name="OutputTail">The last 8 KiB of stdout and stderr together.</param>
public sealed record SandboxResult(int ExitCode, string OutputTail, bool TimedOut, TimeSpan Duration);

/// <summary>Runs one command under limits (ADR 0035): writes only in its workspace, no credential directories, no network
/// unless allowed, a cut environment.</summary>
public interface ISandbox
{
    /// <summary>"seatbelt", "bubblewrap" or "none"; recorded in a verification result.</summary>
    string Kind { get; }

    Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken ct);

    /// <summary>The command line that starts <see cref="SandboxSpec.Argv"/> confined, for a child the caller starts and keeps open (an ACP agent
    /// on its standard input and output, ADR 0086). <see cref="SandboxSpec.Timeout"/> is not applied: the caller owns the child's life.</summary>
    SandboxLaunch Wrap(SandboxSpec spec) => throw new NotSupportedException($"the {Kind} sandbox cannot wrap a long-lived command");
}

/// <summary>What to start for a wrapped command. Dispose it once the child has exited: it removes the private directory the child used as HOME and TMPDIR.</summary>
public sealed class SandboxLaunch(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, string workingDirectory, string tempDirectory) : IDisposable
{
    public string FileName { get; } = fileName;

    public IReadOnlyList<string> Arguments { get; } = arguments;

    /// <summary>The whole environment the child gets; start it with no other variables.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; } = environment;

    public string WorkingDirectory { get; } = workingDirectory;

    public void Dispose()
    {
        try
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: the operating system clears temp directories.
        }
    }
}

/// <param name="Kind">"auto" (default), "seatbelt", "bubblewrap" or "none". "none" runs commands unconfined: an explicit opt-in.</param>
/// <param name="Network">Allow the sandboxed command network access.</param>
/// <param name="Env">Names of environment variables passed to the command besides PATH, LANG, LC_ALL and TERM.</param>
public sealed record SandboxSettings(string Kind = "auto", bool Network = false, IReadOnlyList<string>? Env = null);
