namespace Chargehand.Sandbox;

/// <summary>Linux <c>bwrap</c> (bubblewrap): the filesystem read-only, credential locations hidden, writes only in the workspace and a
/// private temp directory, no network unless allowed (ADR 0035).</summary>
public sealed class BubblewrapSandbox(string? home = null) : ISandbox
{
    private readonly string _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string Kind => "bubblewrap";

    public async Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken ct)
    {
        var temp = Directory.CreateTempSubdirectory("chargehand-sbx-");
        try
        {
            var env = ProcessRunner.Environment(temp.FullName, spec.PassEnv);
            var args = Args(spec, CredentialPaths.Existing(_home), temp.FullName, env);
            return await ProcessRunner.RunAsync("bwrap", args, env, spec.WorkDirectory, spec.Timeout, ct);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
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
