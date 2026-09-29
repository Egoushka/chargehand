namespace Chargehand.Sandbox;

/// <param name="Argv">The command and its arguments; no shell is involved unless the argv names one.</param>
/// <param name="WorkDirectory">The only directory the command may write to, besides a private temp directory.</param>
/// <param name="Network">False by default (ADR 0035): a build that needs the network fails until the profile allows it.</param>
/// <param name="PassEnv">Names of environment variables copied in on top of the fixed allowlist.</param>
public sealed record SandboxSpec(IReadOnlyList<string> Argv, string WorkDirectory, TimeSpan Timeout, bool Network, IReadOnlyList<string> PassEnv);

/// <param name="OutputTail">The last 8 KiB of stdout and stderr together.</param>
public sealed record SandboxResult(int ExitCode, string OutputTail, bool TimedOut, TimeSpan Duration);

/// <summary>Runs one command under limits (ADR 0035): writes only in its workspace, no credential directories, no network
/// unless allowed, a cut environment.</summary>
public interface ISandbox
{
    /// <summary>"seatbelt", "bubblewrap" or "none"; recorded in a verification result.</summary>
    string Kind { get; }

    Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken ct);
}

/// <param name="Kind">"auto" (default), "seatbelt", "bubblewrap" or "none". "none" runs commands unconfined: an explicit opt-in.</param>
/// <param name="Network">Allow the sandboxed command network access.</param>
/// <param name="Env">Names of environment variables passed to the command besides PATH, LANG, LC_ALL and TERM.</param>
public sealed record SandboxSettings(string Kind = "auto", bool Network = false, IReadOnlyList<string>? Env = null);
