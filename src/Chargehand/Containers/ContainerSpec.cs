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
}
