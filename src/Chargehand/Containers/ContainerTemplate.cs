using System.Globalization;
using System.Text.RegularExpressions;

namespace Chargehand.Containers;

/// <summary>The one place a driven-session <c>docker run</c> line is built (ADR 0039). It is pure: the same spec gives the same
/// line, every value is checked against a strict pattern, and what a container may hold or reach is fixed here.</summary>
public static partial class ContainerTemplate
{
    /// <summary>The labels chargehand puts on what it creates, so cleanup and the kill switch find it without any record.</summary>
    public const string RunLabel = "chargehand.run";

    /// <summary>The only environment names a session container gets. A name outside it is refused, so a caller cannot inject
    /// <c>LD_PRELOAD</c>, <c>NODE_OPTIONS</c> or <c>DOCKER_HOST</c>.</summary>
    public static readonly IReadOnlySet<string> AllowedEnv = new HashSet<string>(StringComparer.Ordinal)
    {
        "ANTHROPIC_BASE_URL", "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN",
        "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY", "https_proxy", "http_proxy", "no_proxy",
        "CHARGEHAND_MCP_URL", "CHARGEHAND_RUN_TOKEN", "CHARGEHAND_RUN_ID", "CHARGEHAND_BRANCH", "CHARGEHAND_DRIVEN",
        "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", "DISABLE_AUTOUPDATER", "DISABLE_TELEMETRY",
    };

    /// <summary>The arguments after <c>docker</c>. Environment values are not among them: <paramref name="envFile"/> holds them
    /// (<see cref="EnvFileLines"/>), so they never show in a process listing.</summary>
    public static IReadOnlyList<string> RunArgs(ContainerSpec spec, string envFile)
    {
        Check(RunIdPattern(), spec.RunId, "run id");
        Check(ImagePattern(), spec.ImageDigest, "image (must be name@sha256:<64 hex>)");
        Check(NamePattern(), spec.WorkVolume, "work volume");
        Check(NamePattern(), spec.OutVolume, "out volume");
        Check(NamePattern(), spec.Network, "network");
        Check(UserPattern(), spec.User, "user");
        if (spec.MemoryMb is < 256 or > 65_536)
            throw new ArgumentException("memory must be 256 to 65536 MiB", nameof(spec));
        if (spec.Cpus is < 0.1 or > 32)
            throw new ArgumentException("cpus must be 0.1 to 32", nameof(spec));
        if (spec.Pids is < 16 or > 4096)
            throw new ArgumentException("pids limit must be 16 to 4096", nameof(spec));
        if (spec.Command.Count == 0 || spec.Command.Any(w => w.Length == 0 || w.Contains('\0')))
            throw new ArgumentException("command must be non-empty words with no NUL", nameof(spec));
        if (string.IsNullOrWhiteSpace(envFile) || envFile.StartsWith('-'))
            throw new ArgumentException("env file must be a path", nameof(envFile));

        List<string> args =
        [
            "run", "--detach", "--init",
            "--name", $"chargehand-{spec.RunId}",
            "--label", $"{RunLabel}={spec.RunId}",
            "--read-only",
            "--tmpfs", "/tmp:rw,nosuid,size=1g",
            "--tmpfs", "/home/session:rw,nosuid,size=2g,uid=10001,gid=10001",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--user", spec.User,
            "--pids-limit", spec.Pids.ToString(CultureInfo.InvariantCulture),
            "--memory", $"{spec.MemoryMb.ToString(CultureInfo.InvariantCulture)}m",
            "--cpus", spec.Cpus.ToString("0.##", CultureInfo.InvariantCulture),
            "--network", spec.Network,
            "-v", $"{spec.WorkVolume}:/work",
            "-v", $"{spec.OutVolume}:/out",
            "--workdir", "/work",
            "--env-file", envFile,
            spec.ImageDigest,
            .. spec.Command,
        ];
        return args;
    }

    /// <summary>The arguments after <c>docker</c> for a batch's egress proxy: the server image, its <c>egress</c> verb, read-only, no capabilities,
    /// no mounts, on the batch's internal network only (the caller joins the outside network after it starts).</summary>
    public static IReadOnlyList<string> EgressArgs(EgressSpec spec)
    {
        Check(RunIdPattern(), spec.BatchId, "batch id");
        Check(ImagePattern(), spec.Image, "image (must be name@sha256:<64 hex> or a local image id)");
        Check(NamePattern(), spec.Network, "network");
        if (spec.Port is < 1024 or > 65535)
            throw new ArgumentException("port must be 1024 to 65535", nameof(spec));
        _ = new Chargehand.Egress.AllowlistMatcher(spec.Allow);
        foreach (var forward in spec.Forwards ?? [])
            if (Chargehand.Egress.PortForward.Parse(forward) is null || Chargehand.Egress.PortForward.Parse(forward)!.ListenPort == spec.Port)
                throw new ArgumentException($"invalid forward '{forward}' (listen-port=host:port, a named host, a port of 1024 or more, not the proxy's own)", nameof(spec));
        List<string> line =
        [
            "run", "--detach", "--init",
            "--name", $"chargehand-egress-{spec.BatchId}",
            "--label", $"{RunLabel}={spec.BatchId}",
            "--read-only",
            "--tmpfs", "/tmp:rw,nosuid,size=64m",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--user", "10001:10001",
            "--pids-limit", "256",
            "--memory", "256m",
            "--cpus", "1",
            "--network", spec.Network,
            spec.Image,
            "egress", "--listen", $"0.0.0.0:{spec.Port.ToString(CultureInfo.InvariantCulture)}", "--allow", string.Join(',', spec.Allow),
        ];
        foreach (var forward in spec.Forwards ?? [])
            line.AddRange(["--forward", forward]);
        return line;
    }

    /// <summary>The script the workspace helper runs; the branch and the commit reach it only as arguments (<c>$1</c>, <c>$2</c>), never spliced into its text.
    /// <c>safe.directory</c> is needed because the read-only source belongs to another user; the clone gets no hooks (<c>--template=</c>) and its own commits
    /// run none (<c>core.hooksPath</c>).</summary>
    public const string WorkspaceScript =
        "set -e\n"
        + "git -c safe.directory='*' clone --quiet --no-hardlinks --template= /src /work\n"
        + "git -C /work checkout --quiet -b \"$1\" \"$2\"\n"
        + "git -C /work config user.name chargehand\n"
        + "git -C /work config user.email chargehand@localhost\n"
        + "git -C /work config core.hooksPath /dev/null\n";

    /// <summary>The arguments after <c>docker</c> for a workspace helper: foreground, removed when it ends, no network, a read-only source and one volume.</summary>
    public static IReadOnlyList<string> WorkspaceArgs(WorkspaceSpec spec)
    {
        Check(RunIdPattern(), spec.RunId, "run id");
        Check(ImagePattern(), spec.Image, "image (must be name@sha256:<64 hex> or a local image id)");
        Check(NamePattern(), spec.WorkVolume, "work volume");
        Check(SourcePathPattern(), spec.SourcePath, "source path (a plain absolute path)");
        Check(WorkspaceBranchPattern(), spec.Branch, "branch (chargehand/<name>)");
        Check(CommitPattern(), spec.Commit, "commit (7 to 40 hex characters)");
        return
        [
            "run", "--rm", "--init",
            "--name", $"chargehand-prep-{spec.RunId}",
            "--label", $"{RunLabel}={spec.RunId}",
            "--network", "none",
            "--read-only",
            "--tmpfs", "/tmp:rw,nosuid,size=256m",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--user", "10001:10001",
            "--pids-limit", "128",
            "--memory", "512m",
            "--cpus", "1",
            "-v", $"{spec.SourcePath}:/src:ro",
            "-v", $"{spec.WorkVolume}:/work",
            "--entrypoint", "sh",
            spec.Image,
            "-c", WorkspaceScript, "prepare", spec.Branch, spec.Commit,
        ];
    }

    /// <summary>The env file's lines, <c>NAME=value</c>, in name order. Refuses a name outside <see cref="AllowedEnv"/> and a value
    /// with a line break or NUL (which would add a second variable).</summary>
    public static IReadOnlyList<string> EnvFileLines(IReadOnlyDictionary<string, string> env)
    {
        foreach (var (name, value) in env)
        {
            if (!AllowedEnv.Contains(name))
                throw new ArgumentException($"environment name '{name}' is not one a session container may get", nameof(env));
            if (value.Contains('\n') || value.Contains('\r') || value.Contains('\0'))
                throw new ArgumentException($"environment value of '{name}' has a line break or NUL", nameof(env));
        }
        return env.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}").ToList();
    }

    /// <summary>Whether <paramref name="image"/> is a digest reference (<c>name@sha256:&lt;64 hex&gt;</c>) or a local image id (<c>sha256:&lt;64 hex&gt;</c>).</summary>
    public static bool IsImageReference(string image) => ImagePattern().IsMatch(image);

    /// <summary>Whether <paramref name="name"/> is safe as a container id or name, a volume or a network in a later docker call.</summary>
    public static bool IsPlainName(string name) => NamePattern().IsMatch(name);

    /// <summary>Whether <paramref name="signal"/> is a signal name such as SIGINT.</summary>
    public static bool IsSignal(string signal) => SignalPattern().IsMatch(signal);

    private static void Check(Regex pattern, string value, string what)
    {
        if (!pattern.IsMatch(value))
            throw new ArgumentException($"invalid {what}");
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_.-]{0,62}$")]
    private static partial Regex RunIdPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._/-]*(:[0-9]+)?(/[a-z0-9._-]+)*@sha256:[0-9a-f]{64}$|^sha256:[0-9a-f]{64}$")]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[0-9]{1,9}:[0-9]{1,9}$")]
    private static partial Regex UserPattern();

    [GeneratedRegex(@"^/[A-Za-z0-9_.+@-]+(/[A-Za-z0-9_.+@-]+)*$")]
    private static partial Regex SourcePathPattern();

    [GeneratedRegex(@"^chargehand/[a-z0-9][a-z0-9_-]*(/[a-z0-9][a-z0-9_-]*)*$")]
    private static partial Regex WorkspaceBranchPattern();

    [GeneratedRegex(@"^[0-9a-f]{7,40}$")]
    private static partial Regex CommitPattern();

    [GeneratedRegex(@"^SIG[A-Z0-9]{2,10}$")]
    private static partial Regex SignalPattern();
}
