using System.Globalization;
using System.Text;
using Chargehand.Modules.Research.Internal;

namespace Chargehand.Modules.Research.Api;

/// <summary>macOS <c>sandbox-exec</c> with a generated profile: reads everywhere except credential locations, writes only in the
/// workspace and a private temp directory, no network unless allowed (ADR 0035). Apple marks <c>sandbox-exec</c> deprecated. With
/// <see cref="SandboxSpec.AllowedHosts"/> set, the profile allows outbound network to the loopback allowlist proxy's port only
/// (<see cref="Internal.SandboxEgress"/>, issue #905) instead of the broad <c>(allow network*)</c>: the kernel itself refuses any
/// other destination, proxy-aware client or not, which is the restriction bwrap cannot make on Linux.</summary>
public sealed class SeatbeltSandbox(string? home = null) : ISandbox
{
    private readonly string _home = CredentialPaths.Real(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public string Kind => "seatbelt";

    public async Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken ct)
    {
        using var launch = Wrap(spec);
        return await ProcessRunner.RunAsync(launch.FileName, launch.Arguments, launch.Environment, launch.WorkingDirectory, spec.Timeout, ct);
    }

    public SandboxLaunch Wrap(SandboxSpec spec)
    {
        var temp = Directory.CreateTempSubdirectory("chargehand-sbx-");
        var work = CredentialPaths.Real(spec.WorkDirectory);
        var egress = spec.Network && spec.AllowedHosts is { Count: > 0 } hosts ? SandboxEgress.Start(hosts) : null;
        var profile = Profile(spec with { WorkDirectory = work, WritablePaths = spec.WritablePaths?.Select(CredentialPaths.Real).ToList() },
            _home, CredentialPaths.Real(temp.FullName), egress?.Port);
        var env = ProcessRunner.Environment(temp.FullName, spec.PassEnv, SandboxEgress.Merge(spec.Env, egress));
        return new SandboxLaunch("/usr/bin/sandbox-exec", ["-p", profile, "--", .. spec.Argv], env, work, temp.FullName, egress);
    }

    /// <summary>The profile text. Paths must already be real paths. <paramref name="egressPort"/> narrows network access to that
    /// loopback port only; null keeps the plain on/off switch.</summary>
    internal static string Profile(SandboxSpec spec, string home, string tempDirectory, int? egressPort = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("(version 1)").AppendLine("(deny default)")
            .AppendLine("(allow process-exec*)").AppendLine("(allow process-fork)").AppendLine("(allow signal (target self))")
            .AppendLine("(allow sysctl-read)").AppendLine("(allow mach-lookup)").AppendLine("(allow ipc-posix-shm*)")
            .AppendLine("(allow file-read*)");
        foreach (var relative in CredentialPaths.Relative)
            sb.AppendLine(CultureInfo.InvariantCulture, $"(deny file-read* (subpath \"{Quote(Path.Combine(home, relative))}\"))");
        var writable = new[] { spec.WorkDirectory, tempDirectory }.Concat(spec.WritablePaths ?? []).Select(p => $"(subpath \"{Quote(p)}\")");
        sb.AppendLine(CultureInfo.InvariantCulture, $"(allow file-write* {string.Join(' ', writable)} (literal \"/dev/null\") (literal \"/dev/dtracehelper\"))");
        if (spec.Network && egressPort is { } port)
            sb.AppendLine(CultureInfo.InvariantCulture, $"(allow network-outbound (remote ip \"127.0.0.1:{port}\"))");
        else if (spec.Network)
            sb.AppendLine("(allow network*)");
        return sb.ToString();
    }

    private static string Quote(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
