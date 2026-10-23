using System.Globalization;
using System.Text.RegularExpressions;

namespace Chargehand.Modules.Runners.Api;

/// <summary>The one place a driven-session <c>docker run</c> line is built (ADR 0039). It is pure: the same spec gives the same
/// line, every value is checked against a strict pattern, and what a container may hold or reach is fixed here.</summary>
public static partial class ContainerTemplate
{
    /// <summary>The labels chargehand puts on what it creates, so cleanup and the kill switch find it without any record.</summary>
    public const string RunLabel = "chargehand.run";

    /// <summary>The name a session calls the chargehand server by, through a forward on the egress container: the same in every batch, so the server can list it in <c>http.allowed_hosts</c> (the egress container's own name changes with the batch).</summary>
    public const string CallbackAlias = "chargehand-driven";

    /// <summary>The only environment names a session container gets. A name outside it is refused, so a caller cannot inject
    /// <c>LD_PRELOAD</c>, <c>NODE_OPTIONS</c> or <c>DOCKER_HOST</c>.</summary>
    public static readonly IReadOnlySet<string> AllowedEnv = new HashSet<string>(StringComparer.Ordinal)
    {
        "ANTHROPIC_BASE_URL", "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN",
        "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY", "https_proxy", "http_proxy", "no_proxy",
        "CHARGEHAND_MCP_URL", "CHARGEHAND_RUN_TOKEN", "CHARGEHAND_RUN_ID", "CHARGEHAND_BRANCH", "CHARGEHAND_DRIVEN",
        "CHARGEHAND_REPOSITORY_PATH", "CHARGEHAND_BASE_COMMIT",
        "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", "DISABLE_AUTOUPDATER", "DISABLE_TELEMETRY",
        // ADR 0048: where the package managers keep their cache (each only with its fixed value, see EnvFileLines) and the test services of the batch.
        "NUGET_PACKAGES", "npm_config_cache", "PIP_CACHE_DIR", "GRADLE_USER_HOME", "MAVEN_OPTS", ServicesVariable,
    };

    /// <summary>The test services a session may use, <c>name=name:port</c> separated by commas (ADR 0048).</summary>
    public const string ServicesVariable = "CHARGEHAND_SERVICES";

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
        var caches = spec.Caches ?? [];
        if (caches.Any(c => CacheVolumes.EcosystemOf(c) is null) || caches.Select(CacheVolumes.EcosystemOf).Distinct().Count() != caches.Count)
            throw new ArgumentException("caches must be cache volumes (chargehand-cache-<ecosystem>-<12 hex>), one per ecosystem", nameof(spec));
        CheckHome(spec, caches);

        List<string> args =
        [
            "run", "--detach", "--init",
            "--name", $"chargehand-{spec.RunId}",
            "--label", $"{RunLabel}={spec.RunId}",
            "--read-only",
            "--tmpfs", "/tmp:rw,nosuid,size=1g",
            .. spec.Home is null ? (string[])["--tmpfs", "/home/session:rw,nosuid,size=2g,uid=10001,gid=10001"] : [],
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--user", spec.User,
            "--pids-limit", spec.Pids.ToString(CultureInfo.InvariantCulture),
            "--memory", $"{spec.MemoryMb.ToString(CultureInfo.InvariantCulture)}m",
            "--memory-swap", $"{(2 * spec.MemoryMb).ToString(CultureInfo.InvariantCulture)}m",
            "--cpus", spec.Cpus.ToString("0.##", CultureInfo.InvariantCulture),
            "--network", spec.Network,
            "-v", $"{spec.WorkVolume}:/work",
            "-v", $"{spec.OutVolume}:/out",
            .. caches.SelectMany(c => new[] { "-v", $"{c}:{CacheVolumes.MountRoot}/{CacheVolumes.EcosystemOf(c)}" }),
            .. spec.Home is null ? (string[])[] : HomeMounts(spec.Home),
            "--workdir", "/work",
            "--env-file", envFile,
            .. spec.Install ? (string[])["--entrypoint", InstallEntrypoint] : [],
            spec.ImageDigest,
            .. spec.Command,
        ];
        return args;
    }

    /// <summary>The run id of an install helper ends with this; the helper's work and out volumes are the run's own, without it.</summary>
    public const string InstallSuffix = "-install";

    /// <summary>The program an install helper runs: the image's shell, named by the template so the image's own entry point (the driver's verbs) is not in play.</summary>
    public const string InstallEntrypoint = "sh";

    /// <summary>What <see cref="InstallScript"/> prints before the output of <c>mise ls --current</c>, so the server can tell the tools from the install's own output.</summary>
    public const string ToolsMarker = "--- chargehand tools";

    /// <summary>The one script an install helper runs (ADR 0078): the shell start-up files go first, so nothing a session left there runs; then mise installs what the checkout pins, and the tools in effect
    /// are listed. It has no input: nothing in it comes from a caller.</summary>
    public static readonly string InstallScript = string.Join("; ",
        "set -e",
        $"rm -f {string.Join(' ', HomeVolumes.StartupFiles.Select(f => $"{HomeVolumes.MountPoint}/{f}"))}",
        "mise install --yes",
        $"echo '{ToolsMarker}'",
        "mise ls --current --no-header");

    /// <summary>The words after the image for an install helper.</summary>
    public static readonly IReadOnlyList<string> InstallCommand = ["-c", InstallScript];

    /// <summary>The home volume at the account's home directory, the tmpfs for Claude Code's state on top of it, and the variables that point mise and Claude Code at them. Every value is a constant.</summary>
    private static string[] HomeMounts(string home) =>
    [
        "-v", $"{home}:{HomeVolumes.MountPoint}",
        "--tmpfs", $"{HomeVolumes.ClaudeConfigDir}:rw,nosuid,size=2g,uid=10001,gid=10001",
        .. HomeVolumes.Environment.SelectMany(e => new[] { "-e", $"{e.Name}={e.Value}" }),
    ];

    /// <summary>A home volume is exactly one volume of the home shape, never with ecosystem caches; an install helper is a run's own, with the fixed command and nothing but a proxy in its environment.</summary>
    private static void CheckHome(ContainerSpec spec, IReadOnlyList<string> caches)
    {
        if (spec.Home is { } home && (!HomeVolumes.IsHomeVolume(home) || caches.Count > 0))
            throw new ArgumentException("home must be one home volume (chargehand-cache-<12 hex>) and not combined with caches", nameof(spec));
        if (!spec.Install && spec.Command.SequenceEqual(InstallCommand, StringComparer.Ordinal))
            throw new ArgumentException("only an install helper runs the install script", nameof(spec));
        if (!spec.Install)
            return;
        if (spec.Home is null || !spec.RunId.EndsWith(InstallSuffix, StringComparison.Ordinal) || !spec.Command.SequenceEqual(InstallCommand, StringComparer.Ordinal))
            throw new ArgumentException($"an install helper has a home volume, a run id ending in {InstallSuffix} and the template's own command", nameof(spec));
    }

    /// <summary>The capabilities a database image's entrypoint needs to start as root, fix the ownership of its data directory and drop to its own user; nothing else is kept.</summary>
    private static readonly string[] ServiceCapabilities = ["CHOWN", "DAC_OVERRIDE", "FOWNER", "SETGID", "SETUID"];

    /// <summary>The arguments after <c>docker</c> for a test service (ADR 0048): a pinned image, detached, on the batch's internal network only and reachable there by its name, with
    /// no capability beyond <see cref="ServiceCapabilities"/>, no mount, limits, and a memory-backed <c>/tmp</c> and data directory. The service is a third party's image, so its
    /// root filesystem is not read-only: a database writes to places the image names. It has no route out and holds no credential.</summary>
    public static IReadOnlyList<string> ServiceArgs(ServiceSpec spec, string? envFile)
    {
        Check(RunIdPattern(), spec.BatchId, "batch id");
        Check(ServiceNamePattern(), spec.Name, "service name");
        Check(ImagePattern(), spec.Image, "image (must be name@sha256:<64 hex>)");
        Check(NamePattern(), spec.Network, "network");
        if (spec.Port is < 1 or > 65535)
            throw new ArgumentException("port must be 1 to 65535", nameof(spec));
        if (spec.MemoryMb is < 64 or > 8192)
            throw new ArgumentException("memory must be 64 to 8192 MiB", nameof(spec));
        foreach (var directory in spec.Tmpfs)
            Check(ServiceTmpfsPattern(), directory, "tmpfs directory (a plain absolute path)");
        if (spec.Env.Count > 0 && (string.IsNullOrWhiteSpace(envFile) || envFile.StartsWith('-')))
            throw new ArgumentException("a service with an environment needs an env file path", nameof(envFile));
        _ = ServiceEnvLines(spec.Env);
        return
        [
            "run", "--detach", "--init",
            "--name", spec.ContainerName,
            "--label", $"{RunLabel}={spec.BatchId}",
            "--cap-drop", "ALL",
            .. ServiceCapabilities.SelectMany(c => new[] { "--cap-add", c }),
            "--security-opt", "no-new-privileges",
            "--pids-limit", "256",
            "--memory", $"{spec.MemoryMb.ToString(CultureInfo.InvariantCulture)}m",
            "--memory-swap", $"{spec.MemoryMb.ToString(CultureInfo.InvariantCulture)}m",
            "--cpus", "1",
            "--tmpfs", "/tmp:rw,nosuid,size=256m",
            .. spec.Tmpfs.SelectMany(d => new[] { "--tmpfs", $"{d}:rw,nosuid,size=1g" }),
            "--network", spec.Network,
            "--network-alias", spec.Name,
            .. spec.Env.Count == 0 ? (string[])[] : ["--env-file", envFile!],
            spec.Image,
        ];
    }

    /// <summary>A service's env file lines, <c>NAME=value</c>, in name order. Refuses a name that is not upper case letters, digits and underscores, an <c>LD_</c> name, and a value with a line break or NUL.</summary>
    public static IReadOnlyList<string> ServiceEnvLines(IReadOnlyDictionary<string, string> env)
    {
        foreach (var (name, value) in env)
        {
            if (!ServiceEnvNamePattern().IsMatch(name) || name.StartsWith("LD_", StringComparison.Ordinal))
                throw new ArgumentException($"environment name '{name}' is not one a service may get", nameof(env));
            if (value.Contains('\n') || value.Contains('\r') || value.Contains('\0'))
                throw new ArgumentException($"environment value of '{name}' has a line break or NUL", nameof(env));
        }
        return env.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}").ToList();
    }

    /// <summary>The arguments after <c>docker</c> for a batch's egress proxy: the server image, its <c>egress</c> verb, read-only, no capabilities,
    /// no mounts, on the batch's internal network only (the caller joins the outside network after it starts).</summary>
    public static IReadOnlyList<string> EgressArgs(EgressSpec spec, string? envFile = null)
    {
        Check(RunIdPattern(), spec.BatchId, "batch id");
        Check(ImagePattern(), spec.Image, "image (must be name@sha256:<64 hex> or a local image id)");
        Check(NamePattern(), spec.Network, "network");
        if (spec.Port is < 1024 or > 65535)
            throw new ArgumentException("port must be 1024 to 65535", nameof(spec));
        _ = new Chargehand.Modules.Gateway.Api.AllowlistMatcher(spec.Allow);
        foreach (var forward in spec.Forwards ?? [])
            if (Chargehand.Modules.Gateway.Api.PortForward.Parse(forward) is null || Chargehand.Modules.Gateway.Api.PortForward.Parse(forward)!.ListenPort == spec.Port)
                throw new ArgumentException($"invalid forward '{forward}' (listen-port=host:port, a named host, a port of 1024 or more, not the proxy's own)", nameof(spec));
        if (spec.Gateway is { } gateway)
            CheckGateway(spec, gateway, envFile);
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
            "--network-alias", CallbackAlias,
            .. spec.Gateway is null ? (string[])[] : ["--env-file", envFile!],
            spec.Image,
            "egress", "--listen", $"0.0.0.0:{spec.Port.ToString(CultureInfo.InvariantCulture)}", "--allow", string.Join(',', spec.Allow),
        ];
        foreach (var forward in spec.Forwards ?? [])
            line.AddRange(["--forward", forward]);
        if (spec.Gateway is { } model)
            line.AddRange(["--model-listen", $"0.0.0.0:{model.Port.ToString(CultureInfo.InvariantCulture)}", "--model-host", model.Host]);
        return line;
    }

    private static void CheckGateway(EgressSpec spec, ModelGatewaySpec gateway, string? envFile)
    {
        if (gateway.Port < 1024 || gateway.Port > 65535 || gateway.Port == spec.Port || (spec.Forwards ?? []).Any(f => Chargehand.Modules.Gateway.Api.PortForward.Parse(f)!.ListenPort == gateway.Port))
            throw new ArgumentException("the model port must be 1024 to 65535 and not the proxy's or a forward's", nameof(spec));
        if (gateway.TokenKey.Length != 64 || !gateway.TokenKey.All(Uri.IsHexDigit))
            throw new ArgumentException("the token key must be 64 hex characters", nameof(spec));
        if (gateway.Credential.Length == 0 || gateway.Credential.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new ArgumentException("the model credential is empty or has whitespace or a control character", nameof(spec));
        if (Chargehand.Modules.Gateway.Api.ModelGateway.RefuseHost(gateway.Host) is { } refused)
            throw new ArgumentException($"the model host is not acceptable: {refused}", nameof(spec));
        if (string.IsNullOrWhiteSpace(envFile) || envFile.StartsWith('-'))
            throw new ArgumentException("an egress container with a model endpoint needs an env file path", nameof(envFile));
    }

    /// <summary>The egress container's env file lines: the model credential and the run-token key, for <see cref="EgressArgs"/>'s <c>--env-file</c>.</summary>
    public static IReadOnlyList<string> EgressEnvLines(ModelGatewaySpec gateway) =>
        [$"{Chargehand.Modules.Gateway.Api.EgressCli.CredentialVariable}={gateway.Credential}", $"{Chargehand.Modules.Gateway.Api.EgressCli.KeyVariable}={gateway.TokenKey}"];

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
            if (CacheVolumes.IsCacheVariable(name, out var fixedValue) && value != fixedValue)
                throw new ArgumentException($"environment value of '{name}' must be {fixedValue}", nameof(env));
            if (name == ServicesVariable && !ServicesValuePattern().IsMatch(value))
                throw new ArgumentException($"environment value of '{name}' must be name=name:port pairs", nameof(env));
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

    [GeneratedRegex(@"^[a-z][a-z0-9]{0,23}$")]
    private static partial Regex ServiceNamePattern();

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{0,63}$")]
    private static partial Regex ServiceEnvNamePattern();

    [GeneratedRegex(@"^/(?!\.\.?(/|$))[A-Za-z0-9_.+@-]+(/(?!\.\.?(/|$))[A-Za-z0-9_.+@-]+)*$")]
    private static partial Regex ServiceTmpfsPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9]{0,23}=[a-z][a-z0-9]{0,23}:[0-9]{1,5}(,[a-z][a-z0-9]{0,23}=[a-z][a-z0-9]{0,23}:[0-9]{1,5})*$")]
    private static partial Regex ServicesValuePattern();

    [GeneratedRegex(@"^SIG[A-Z0-9]{2,10}$")]
    private static partial Regex SignalPattern();
}
