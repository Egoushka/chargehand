namespace Chargehand.Runner;

/// <summary>What the runner will start on its engine (ADR 0039). Every check is a refusal with a reason; the runner builds the
/// <c>docker run</c> line itself from <see cref="Containers.ContainerTemplate"/>, so a caller can name a run, an image from this list and
/// numbers under these ceilings, and nothing else.</summary>
/// <param name="Images">Session images by digest; the only images that start.</param>
/// <param name="EgressImage">The egress proxy's image; the caller never names it.</param>
/// <param name="MaxContainers">Labelled containers that may exist at once, running or not.</param>
public sealed record RunnerPolicy(IReadOnlyList<string> Images, string EgressImage, int MaxContainers = 8, int MaxMemoryMb = 16_384, double MaxCpus = 4, int MaxPids = 1024)
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

    public static string? RefuseVolume(string name) =>
        name.StartsWith(WorkPrefix, StringComparison.Ordinal) || name.StartsWith(OutPrefix, StringComparison.Ordinal) ? null : $"a volume must start with {WorkPrefix} or {OutPrefix}";

    public static string? RefuseNetwork(string name) =>
        name.StartsWith(NetworkPrefix, StringComparison.Ordinal) ? null : $"a network must start with {NetworkPrefix}";

    public static string? RefuseConnect(string network) =>
        network == OutsideNetwork ? null : $"a container may only be joined to {OutsideNetwork}";
}
