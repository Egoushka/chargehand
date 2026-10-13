namespace Chargehand.Runner;

/// <summary>What the runner will start on its engine (ADR 0039). Every check is a refusal with a reason; the runner builds the
/// <c>docker run</c> line itself from <see cref="Containers.ContainerTemplate"/>, so a caller can name a run, an image from this list and
/// numbers under these ceilings, and nothing else.</summary>
/// <param name="Images">Session images by digest; the only images that start.</param>
/// <param name="EgressImage">The egress proxy's image; the caller never names it.</param>
/// <param name="MaxContainers">Labelled containers that may exist at once, running or not.</param>
/// <param name="OutsideNetworks">Networks besides <c>bridge</c> a session container's egress proxy may be joined to: the network the chargehand server sits on, so a session can reach it through a forward.</param>
/// <param name="SourceRoots">Directories a workspace's read-only source may be under (chargehand's checkouts). None: no workspace is prepared, because a source path names a host directory.</param>
/// <param name="ServiceImages">Images a test service may be started from (ADR 0048), by digest. None: the runner starts no service.</param>
/// <param name="MaxBundleBytes">The most a repository bundle the server sends may be (ADR 0058): the size of <c>source.bundle</c>, which is streamed into the output volume and never held in memory.</param>
/// <param name="Forwards">The forwards (<c>listen-port=host:port</c>) an egress container may carry: how a session calls the chargehand server back. The server names them in its request and the runner starts only these, so a server cannot point an egress container at any other host. None: an egress container carries no forward.</param>
public sealed record RunnerPolicy(IReadOnlyList<string> Images, string EgressImage, int MaxContainers = 8, int MaxMemoryMb = 16_384, double MaxCpus = 4, int MaxPids = 1024,
    IReadOnlyList<string>? SourceRoots = null, IReadOnlyList<string>? OutsideNetworks = null, IReadOnlyList<string>? Forwards = null,
    IReadOnlyList<string>? ServiceImages = null, long MaxBundleBytes = RunnerPolicy.DefaultMaxBundleBytes)
{
    public const long DefaultMaxBundleBytes = 512L * 1024 * 1024;

    public const string WorkPrefix = "chargehand-work-";
    public const string OutPrefix = "chargehand-out-";
    public const string NetworkPrefix = "chargehand-net-";

    /// <summary>The network a container may be joined to besides its batch network.</summary>
    public const string OutsideNetwork = "bridge";

    public string? RefuseStart(RunnerStart start)
    {
        if (!Images.Contains(start.Image, StringComparer.Ordinal))
            return "image is not on the runner's allowlist";
        if (start.WorkVolume != WorkPrefix + start.RunId || start.OutVolume != OutPrefix + start.RunId)
            return $"volumes must be {WorkPrefix}<run id> and {OutPrefix}<run id>";
        if (!start.Network.StartsWith(NetworkPrefix, StringComparison.Ordinal))
            return $"the network must be a batch network ({NetworkPrefix}<batch id>)";
        if (start.Caches?.FirstOrDefault(c => Containers.CacheVolumes.EcosystemOf(c) is null) is { } cache)
            return $"'{cache}' is not a cache volume ({Containers.CacheVolumes.Prefix}<ecosystem>-<12 hex>)";
        if (start.MemoryMb > MaxMemoryMb || start.Cpus > MaxCpus || start.Pids > MaxPids)
            return $"limits are above the runner's ceilings (memory {MaxMemoryMb} MiB, cpus {MaxCpus}, pids {MaxPids})";
        return null;
    }

    /// <summary>A test service starts from an image the runner lists for it, on a batch network, under the ceilings of a session container.</summary>
    public string? RefuseService(RunnerService service)
    {
        if (ServiceImages?.Contains(service.Image, StringComparer.Ordinal) != true)
            return "image is not on the runner's service image allowlist (--service-images)";
        if (!service.Network.StartsWith(NetworkPrefix, StringComparison.Ordinal))
            return $"the network must be a batch network ({NetworkPrefix}<batch id>)";
        if (service.MemoryMb > MaxMemoryMb)
            return $"memory is above the runner's ceiling ({MaxMemoryMb} MiB)";
        return null;
    }

    /// <summary>A workspace helper mounts a host directory read-only: it must be a directory strictly under a configured root, by its normalised path, so <c>..</c> and a
    /// sibling that shares a prefix do not pass.</summary>
    public string? RefuseWorkspace(RunnerWorkspace workspace)
    {
        if (!Images.Contains(workspace.Image, StringComparer.Ordinal))
            return "image is not on the runner's allowlist";
        if (workspace.WorkVolume != WorkPrefix + workspace.RunId)
            return $"the volume must be {WorkPrefix}<run id>";
        if (SourceRoots is not { Count: > 0 })
            return "the runner has no source roots configured, so it prepares no workspace";
        if (workspace.SourcePath.Split('/').Contains(".."))
            return "the source path may not contain '..'";
        var path = Path.TrimEndingDirectorySeparator(workspace.SourcePath);
        return SourceRoots.Any(root => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + "/", StringComparison.Ordinal))
            ? null
            : "the source is not under one of the runner's source roots";
    }

    /// <summary>A workspace from a bundle (ADR 0058) names no host path, so it needs no source root: a listed image, the run's work volume and a branch and commit the template checks.</summary>
    public string? RefuseBundleWorkspace(RunnerBundleWorkspace workspace)
    {
        if (!Images.Contains(workspace.Image, StringComparer.Ordinal))
            return "image is not on the runner's allowlist";
        return workspace.WorkVolume == WorkPrefix + workspace.RunId ? null : $"the volume must be {WorkPrefix}<run id>";
    }

    /// <summary>The repository bundle's size, which the request states before a byte is read, must be within <see cref="MaxBundleBytes"/>.</summary>
    public string? RefuseBundleSize(long contentLength) => contentLength switch
    {
        < 0 => "the bundle's Content-Length is negative",
        var length when length > MaxBundleBytes => $"the bundle is {Size(length)}, above the runner's limit of {Size(MaxBundleBytes)} (--max-bundle-mb)",
        _ => null,
    };

    private static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024)} MiB" : $"{bytes} bytes";

    /// <summary>A file helper touches one run's output volume: a listed image and a fixed name, <c>task.json</c> and <c>seed.bundle</c> in and the result files out, whatever the caller asks.</summary>
    public string? RefuseOutFile(string image, string name, bool write)
    {
        if (!Images.Contains(image, StringComparer.Ordinal))
            return "image is not on the runner's allowlist";
        return (write ? Containers.ContainerTemplate.OutFilesIn : Containers.ContainerTemplate.OutFilesOut).Contains(name, StringComparer.Ordinal)
            ? null
            : $"the runner {(write ? "writes" : "reads")} only {string.Join(", ", write ? Containers.ContainerTemplate.OutFilesIn : Containers.ContainerTemplate.OutFilesOut)}";
    }

    public static string? RefuseVolume(string name) =>
        name.StartsWith(WorkPrefix, StringComparison.Ordinal) || name.StartsWith(OutPrefix, StringComparison.Ordinal) ? null : $"a volume must start with {WorkPrefix} or {OutPrefix}";

    public static string? RefuseNetwork(string name) =>
        name.StartsWith(NetworkPrefix, StringComparison.Ordinal) ? null : $"a network must start with {NetworkPrefix}";

    public string? RefuseForwards(IReadOnlyList<string>? requested) =>
        requested is null || requested.All(f => Forwards?.Contains(f, StringComparer.Ordinal) ?? false) ? null : "a forward may only be one the runner is configured for (--forwards)";

    public string? RefuseConnect(string network) =>
        network == OutsideNetwork || (OutsideNetworks?.Contains(network) ?? false) ? null : $"a container may only be joined to {OutsideNetwork} or a configured outside network";
}
