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
    string User = "10001:10001");

/// <summary>The egress proxy's container (ADR 0039): the server image started with its <c>egress</c> verb, one per batch.</summary>
/// <param name="Allow">Host patterns for <see cref="Chargehand.Egress.AllowlistMatcher"/>.</param>
public sealed record EgressSpec(string BatchId, string Image, string Network, IReadOnlyList<string> Allow, int Port = 3128);

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

public enum ContainerStatus { Running, Exited, Missing }

/// <param name="ExitCode">Set once the container has exited.</param>
/// <param name="OomKilled">The kernel killed it for memory.</param>
public sealed record ContainerState(ContainerStatus Status, int? ExitCode, bool OomKilled);

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
}
