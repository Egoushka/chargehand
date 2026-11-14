using Chargehand.Modules.Research.Internal;
namespace Chargehand.Modules.Research.Api;

/// <summary>Linux <c>bwrap</c> (bubblewrap): the filesystem read-only, credential locations hidden, writes only in the workspace and a
/// private temp directory, no network unless allowed (ADR 0035). With <see cref="SandboxSpec.AllowedHosts"/> set, the command is
/// handed HTTPS_PROXY/HTTP_PROXY for a loopback allowlist proxy (<see cref="Internal.SandboxEgress"/>, issue #905): a proxy-aware
/// client (npm, NuGet, curl) reaching an unlisted host is refused there. The child still gets <c>--share-net</c>, the host's own
/// network namespace: bwrap has no "reach only this loopback port" primitive, and building a private network namespace with its own
/// route would need CAP_NET_ADMIN this process does not have, so a client that opens a raw socket and ignores the proxy is not
/// stopped by this alone (see docs/adr, the per-host allowlist decision).</summary>
public sealed class BubblewrapSandbox(string? home = null) : ISandbox
{
    private readonly string _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string Kind => "bubblewrap";

    public async Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken ct)
    {
        using var launch = Wrap(spec);
        return await ProcessRunner.RunAsync(launch.FileName, launch.Arguments, launch.Environment, launch.WorkingDirectory, spec.Timeout, ct);
    }

    public SandboxLaunch Wrap(SandboxSpec spec)
    {
        var temp = Directory.CreateTempSubdirectory("chargehand-sbx-");
        var egress = spec.Network && spec.AllowedHosts is { Count: > 0 } hosts ? SandboxEgress.Start(hosts) : null;
        var env = ProcessRunner.Environment(temp.FullName, spec.PassEnv, SandboxEgress.Merge(spec.Env, egress));
        return new SandboxLaunch("bwrap", Args(spec, CredentialPaths.Existing(_home), temp.FullName, env), env, spec.WorkDirectory, temp.FullName, egress);
    }

    /// <summary>The bwrap arguments. <paramref name="credentials"/> are the existing credential paths: a directory is hidden behind an
    /// empty tmpfs, a file behind /dev/null.</summary>
    internal static List<string> Args(SandboxSpec spec, IReadOnlyList<string> credentials, string tempDirectory, IReadOnlyDictionary<string, string> env)
    {
        var args = new List<string> { "--ro-bind", "/", "/", "--dev", "/dev", "--proc", "/proc" };
        foreach (var path in credentials)
        {
            if (Directory.Exists(path))
                args.AddRange(["--tmpfs", path]);
            else
                args.AddRange(["--ro-bind", "/dev/null", path]);
        }
        args.AddRange(["--bind", spec.WorkDirectory, spec.WorkDirectory, "--bind", tempDirectory, tempDirectory, "--unshare-all"]);
        foreach (var path in spec.WritablePaths ?? [])
            args.AddRange(["--bind", path, path]);
        if (spec.Network)
            args.Add("--share-net");
        args.AddRange(["--die-with-parent", "--chdir", spec.WorkDirectory, "--clearenv"]);
        foreach (var (k, v) in env)
            args.AddRange(["--setenv", k, v]);
        args.Add("--");
        args.AddRange(spec.Argv);
        return args;
    }
}
