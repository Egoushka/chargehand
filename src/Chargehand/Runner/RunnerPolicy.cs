namespace Chargehand.Runner;

/// <summary>What the runner will start on its engine (ADR 0039). Every check is a refusal with a reason; the runner builds the
/// <c>docker run</c> line itself from <see cref="Containers.ContainerTemplate"/>, so a caller can name a run, an image from this list and
/// numbers under these ceilings, and nothing else.</summary>
/// <param name="Images">Session images by digest; the only images that start.</param>
/// <param name="EgressImage">The egress proxy's image; the caller never names it.</param>
/// <param name="MaxContainers">Labelled containers that may exist at once, running or not.</param>
/// <param name="OutsideNetworks">Networks besides <c>bridge</c> a session container's egress proxy may be joined to: the network the chargehand server sits on, so a session can reach it through a forward.</param>
/// <param name="SourceRoots">Directories a workspace's read-only source may be under (chargehand's checkouts). None: no workspace is prepared, because a source path names a host directory.</param>
public sealed record RunnerPolicy(IReadOnlyList<string> Images, string EgressImage, int MaxContainers = 8, int MaxMemoryMb = 16_384, double MaxCpus = 4, int MaxPids = 1024,
    IReadOnlyList<string>? SourceRoots = null, IReadOnlyList<string>? OutsideNetworks = null)
{
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
        if (start.MemoryMb > MaxMemoryMb || start.Cpus > MaxCpus || start.Pids > MaxPids)
            return $"limits are above the runner's ceilings (memory {MaxMemoryMb} MiB, cpus {MaxCpus}, pids {MaxPids})";
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

    public static string? RefuseVolume(string name) =>
        name.StartsWith(WorkPrefix, StringComparison.Ordinal) || name.StartsWith(OutPrefix, StringComparison.Ordinal) ? null : $"a volume must start with {WorkPrefix} or {OutPrefix}";

    public static string? RefuseNetwork(string name) =>
        name.StartsWith(NetworkPrefix, StringComparison.Ordinal) ? null : $"a network must start with {NetworkPrefix}";

    public string? RefuseConnect(string network) =>
        network == OutsideNetwork || (OutsideNetworks?.Contains(network) ?? false) ? null : $"a container may only be joined to {OutsideNetwork} or a configured outside network";
}
