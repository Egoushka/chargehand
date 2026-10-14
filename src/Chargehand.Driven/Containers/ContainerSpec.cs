namespace Chargehand.Containers;

/// <summary>What one driven-session container is (ADR 0039). Everything else about its <c>docker run</c> line is fixed by
/// <see cref="ContainerTemplate"/>; a caller cannot add a flag, a mount, a network mode or a capability.</summary>
/// <param name="RunId">A plain token; the container's name and its <c>chargehand.run</c> label.</param>
/// <param name="ImageDigest">An image by digest, <c>name@sha256:&lt;64 hex&gt;</c>. A tag is refused: it can move.</param>
/// <param name="WorkVolume">A named volume mounted read-write at <c>/work</c>.</param>
/// <param name="OutVolume">A named volume mounted read-write at <c>/out</c>: the bundle, the report and the stream log.</param>
/// <param name="Network">The name of the per-batch internal network; the container is attached to nothing else.</param>
/// <param name="Env">Names from <see cref="ContainerTemplate.AllowedEnv"/> only; values go through an env file, never the command line.</param>
/// <param name="Command">Arguments after the image; a word that looks like a flag stays the command's, not docker's.</param>
/// <param name="Caches">Package cache volumes by name (<see cref="CacheVolumes.Name"/>), each mounted read-write at <c>/cache/&lt;ecosystem&gt;</c> (ADR 0048); null: none.</param>
public sealed record ContainerSpec(
    string RunId,
    string ImageDigest,
    string WorkVolume,
    string OutVolume,
    string Network,
    IReadOnlyDictionary<string, string> Env,
    int MemoryMb,
    double Cpus,
    int Pids,
    IReadOnlyList<string> Command,
    string User = "10001:10001",
    IReadOnlyList<string>? Caches = null);

/// <summary>A test service of a batch (ADR 0048): a pinned third-party image on the batch's internal network, with no route out, removed with the batch.</summary>
/// <param name="Name">The host name on the batch network (an alias), lowercase letters and digits.</param>
/// <param name="Env">The service's own environment; values go through an env file.</param>
/// <param name="Tmpfs">Memory-backed directories of the image (a database's data directory).</param>
public sealed record ServiceSpec(string BatchId, string Name, string Image, string Network, int Port, IReadOnlyDictionary<string, string> Env, IReadOnlyList<string> Tmpfs, int MemoryMb = 512)
{
    /// <summary>The container's name; the service name has no hyphen, so the batch id can be read back from it (the sweep for leftovers does).</summary>
    public string ContainerName => $"chargehand-svc-{Name}-{BatchId}";
}

/// <summary>Starts a batch's test services. The Docker CLI and the runner implement it; an engine without it cannot run a batch whose repository declares services.</summary>
public interface IServiceEngine
{
    /// <returns>The container id.</returns>
    Task<string> StartServiceAsync(ServiceSpec spec, CancellationToken ct);
}

/// <summary>The egress proxy's container (ADR 0039): the server image started with its <c>egress</c> verb, one per batch.</summary>
/// <param name="Allow">Host patterns for <see cref="Chargehand.Modules.Gateway.Api.AllowlistMatcher"/>.</param>
/// <param name="Forwards">Operator-named forwards, <c>listen-port=host:port</c>: how a session reaches the chargehand server from the internal network.</param>
/// <param name="Gateway">The model endpoint (the credential exchange); null: the container has none.</param>
public sealed record EgressSpec(string BatchId, string Image, string Network, IReadOnlyList<string> Allow, int Port = 3128, IReadOnlyList<string>? Forwards = null,
    ModelGatewaySpec? Gateway = null);

/// <summary>What the egress container needs to exchange a run token for the model credential (ADR 0039, decision 9). Both secrets travel in the container's env file, never on a command line.</summary>
/// <param name="Credential">The real model credential. It lives in the egress container and nowhere else: not in a session container, not in an image, not in a log.</param>
/// <param name="TokenKey">The batch's key for run tokens, 64 hex characters (<c>ModelTokens.NewKey</c>).</param>
/// <param name="Host">The one host requests are forwarded to.</param>
public sealed record ModelGatewaySpec(string Credential, string TokenKey, string Host = Chargehand.Modules.Gateway.Api.EgressCli.DefaultModelHost, int Port = ModelGatewaySpec.DefaultPort)
{
    public const int DefaultPort = 3129;
}

/// <summary>The helper that fills a session's workspace volume (ADR 0039): a clone of the read-only source on a new branch at a commit, made in a container
/// that has no network and can write only the volume.</summary>
/// <param name="Image">A session image (it has git).</param>
/// <param name="SourcePath">An absolute host path, mounted read-only: the worker's checkout of the pinned commit (ADR 0023).</param>
/// <param name="WorkVolume">A new, empty named volume.</param>
/// <param name="Branch">Created at <paramref name="Commit"/>; under <c>chargehand/</c>.</param>
public sealed record WorkspaceSpec(string RunId, string Image, string SourcePath, string WorkVolume, string Branch, string Commit);

/// <summary>Fills a workspace volume before a session starts, and again for the fresh verification run after it.</summary>
public interface IWorkspaceEngine
{
    Task PrepareWorkspaceAsync(WorkspaceSpec spec, CancellationToken ct);
}

/// <summary>The helper that fills a workspace volume on an engine that cannot see the server's checkouts (ADR 0058): the same clone and branch as <see cref="WorkspaceSpec"/>, made from
/// <c>source.bundle</c> in the run's output volume, which the server sent first.</summary>
/// <param name="Image">A session image (it has git).</param>
/// <param name="OutVolume">The run's output volume, mounted read-only; it holds <see cref="ContainerTemplate.SourceBundleFile"/>.</param>
/// <param name="WorkVolume">A new, empty named volume.</param>
/// <param name="Branch">Created at <paramref name="Commit"/>; under <c>chargehand/</c>.</param>
public sealed record BundleWorkspaceSpec(string RunId, string Image, string OutVolume, string WorkVolume, string Branch, string Commit);

/// <summary>Fills a workspace volume from a bundle in the run's output volume. The Docker CLI and the runner implement it.</summary>
public interface IBundleWorkspaceEngine
{
    Task PrepareBundleWorkspaceAsync(BundleWorkspaceSpec spec, CancellationToken ct);
}

/// <summary>One file of a session's output volume, moved by a throwaway helper (ADR 0039).</summary>
/// <param name="Image">A session image (it has <c>sh</c> and <c>cat</c>).</param>
/// <param name="OutVolume">The run's output volume.</param>
/// <param name="Name">One of <see cref="ContainerTemplate.OutFilesIn"/> to write, <see cref="ContainerTemplate.OutFilesOut"/> to read.</param>
public sealed record OutFileSpec(string RunId, string Image, string OutVolume, string Name);

/// <summary>Puts the task file into a session's output volume before the session starts and reads its results out after it ends.</summary>
public interface IOutVolumeEngine
{
    Task WriteOutFileAsync(OutFileSpec spec, ReadOnlyMemory<byte> content, CancellationToken ct);

    /// <summary>Writes a file of <paramref name="length"/> bytes that is too large to hold in memory (the repository's bundle, ADR 0058). The default reads it into memory; the Docker CLI and the runner
    /// client pass it on as a stream.</summary>
    async Task WriteOutStreamAsync(OutFileSpec spec, Stream content, long length, CancellationToken ct)
    {
        using var buffer = new MemoryStream(checked((int)length));
        await content.CopyToAsync(buffer, ct);
        await WriteOutFileAsync(spec, buffer.ToArray(), ct);
    }

    /// <returns>False when the volume has no such file; nothing was written to <paramref name="destination"/> then.</returns>
    Task<bool> ReadOutFileAsync(OutFileSpec spec, Stream destination, CancellationToken ct);
}

/// <summary>Reads the end of a file in a session's output volume (ADR 0059): at most <paramref name="maxBytes"/> of it, for the files of <see cref="ContainerTemplate.OutFilesTail"/> only. It is separate from
/// <see cref="IOutVolumeEngine"/> so that an engine, or a runner, that predates it is simply not asked.</summary>
public interface IOutTailEngine
{
    /// <returns>False when the volume has no such file; nothing was written to <paramref name="destination"/> then.</returns>
    Task<bool> ReadOutTailAsync(OutFileSpec spec, int maxBytes, Stream destination, CancellationToken ct);
}

/// <summary><see cref="Created"/>: made and never started, what a <c>docker run</c> cut short between its create and its start leaves.</summary>
public enum ContainerStatus { Running, Exited, Missing, Created }

/// <param name="ExitCode">Set once the container has exited.</param>
/// <param name="OomKilled">The kernel killed it for memory.</param>
/// <param name="MemoryPeakMb">The most memory the container has used so far, in MiB: its cgroup's <c>memory.peak</c>, else the largest sampled use. Null when the engine could not measure it.</param>
public sealed record ContainerState(ContainerStatus Status, int? ExitCode, bool OomKilled, int? MemoryPeakMb = null);

/// <summary>Starts, signals, inspects and removes session containers (ADR 0039). The Docker CLI implements it on a machine that has
/// Docker; the runner service (a later task) implements it over HTTP where the server must not hold the socket.</summary>
public interface IContainerEngine
{
    /// <returns>The container id.</returns>
    Task<string> StartAsync(ContainerSpec spec, CancellationToken ct);

    /// <param name="signal">A signal name such as <c>SIGINT</c>.</param>
    Task SignalAsync(string id, string signal, CancellationToken ct);

    Task<ContainerState> InspectAsync(string id, CancellationToken ct);

    Task RemoveAsync(string id, CancellationToken ct);

    /// <summary>Removes every container carrying the <c>chargehand.run</c> label: the kill switch that works without the server's own records.</summary>
    Task KillAllAsync(CancellationToken ct);

    /// <returns>The end of the container's combined output, at most <paramref name="bytes"/> bytes.</returns>
    Task<string> LogsTailAsync(string id, int bytes, CancellationToken ct);

    Task CreateVolumeAsync(string name, string runId, CancellationToken ct);

    Task RemoveVolumeAsync(string name, CancellationToken ct);

    /// <summary>Creates an internal network (no route out) labelled with the batch.</summary>
    Task CreateNetworkAsync(string name, string batchId, CancellationToken ct);

    Task RemoveNetworkAsync(string name, CancellationToken ct);

    /// <summary>Starts a batch's egress proxy on <see cref="EgressSpec.Network"/>; the caller then joins it to the outside network.</summary>
    Task<string> StartEgressAsync(EgressSpec spec, CancellationToken ct);

    Task ConnectNetworkAsync(string container, string network, CancellationToken ct);

    /// <summary>Whether the container carries the <c>chargehand.run</c> label: the runner touches nothing else on a shared engine.</summary>
    Task<bool> OwnsAsync(string id, CancellationToken ct);

    /// <summary>How many labelled containers exist, running or not.</summary>
    Task<int> CountAsync(CancellationToken ct);

    /// <summary>The names of every labelled container, network and volume: what a restarted server sweeps for leftovers (ADR 0043).</summary>
    Task<EngineResources> ListAsync(CancellationToken ct);
}

public sealed record EngineResources(IReadOnlyList<string> Containers, IReadOnlyList<string> Networks, IReadOnlyList<string> Volumes);
