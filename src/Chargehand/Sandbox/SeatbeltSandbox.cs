using System.Globalization;
using System.Text;

namespace Chargehand.Sandbox;

/// <summary>macOS <c>sandbox-exec</c> with a generated profile: reads everywhere except credential locations, writes only in the
/// workspace and a private temp directory, no network unless allowed (ADR 0035). Apple marks <c>sandbox-exec</c> deprecated.</summary>
public sealed class SeatbeltSandbox(string? home = null) : ISandbox
{
    private readonly string _home = CredentialPaths.Real(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public string Kind => "seatbelt";

    public async Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken ct)
    {
        var temp = Directory.CreateTempSubdirectory("chargehand-sbx-");
        try
        {
            var work = CredentialPaths.Real(spec.WorkDirectory);
            var profile = Profile(spec with { WorkDirectory = work }, _home, CredentialPaths.Real(temp.FullName));
            return await ProcessRunner.RunAsync("/usr/bin/sandbox-exec", ["-p", profile, "--", .. spec.Argv], ProcessRunner.Environment(temp.FullName, spec.PassEnv), work,
                spec.Timeout, ct);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    /// <summary>The profile text. Paths must already be real paths.</summary>
    internal static string Profile(SandboxSpec spec, string home, string tempDirectory)
    {
        var sb = new StringBuilder();
        sb.AppendLine("(version 1)").AppendLine("(deny default)")
            .AppendLine("(allow process-exec*)").AppendLine("(allow process-fork)").AppendLine("(allow signal (target self))")
            .AppendLine("(allow sysctl-read)").AppendLine("(allow mach-lookup)").AppendLine("(allow ipc-posix-shm*)")
            .AppendLine("(allow file-read*)");
        foreach (var relative in CredentialPaths.Relative)
            sb.AppendLine(CultureInfo.InvariantCulture, $"(deny file-read* (subpath \"{Quote(Path.Combine(home, relative))}\"))");
        sb.AppendLine(CultureInfo.InvariantCulture, $"(allow file-write* (subpath \"{Quote(spec.WorkDirectory)}\") (subpath \"{Quote(tempDirectory)}\") (literal \"/dev/null\") (literal \"/dev/dtracehelper\"))");
        if (spec.Network)
            sb.AppendLine("(allow network*)");
        return sb.ToString();
    }

    private static string Quote(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
