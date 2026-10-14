using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Modules.Sessions.Api;

/// <summary>How a session's output volume is written and read (ADR 0039): the task file goes in before the container starts, and after it ends the bundle, the report
/// and the outcome come out into a directory chargehand owns.</summary>
public interface ISessionVolumes
{
    Task WriteTaskAsync(string runId, SessionTask task, CancellationToken ct);

    /// <summary>Puts the previous session's bundle into the volume as <c>seed.bundle</c>; the continuation session starts its branch from it (ADR 0044).</summary>
    Task WriteSeedAsync(string runId, string bundlePath, CancellationToken ct);

    /// <summary>Copies <c>chargehand.bundle</c>, <c>driven-report.json</c>, <c>session-outcome.json</c> and a stopped session's <c>handoff.json</c> and <c>progress.md</c> (each only if it exists;
    /// a runner too old to know the last two is not an error) from the volume into <paramref name="directory"/>.</summary>
    Task FetchOutputAsync(string runId, string directory, CancellationToken ct);

    /// <summary>The session's running tally while it works; null if it has not written one yet or the file is unreadable.</summary>
    Task<TaskUsage?> ReadUsageAsync(string runId, CancellationToken ct);

    /// <summary>At most <paramref name="maxBytes"/> from the end of the session's transcript (<c>stream.jsonl</c>, ADR 0059), for the console; null if the volume has no transcript, or if this deployment cannot read the end
    /// of a file (the default). It is not part of a task's own work: nothing in a batch reads it.</summary>
    /// <exception cref="ChargehandException">The runner refused the read or could not be reached.</exception>
    Task<byte[]?> ReadTranscriptTailAsync(string runId, int maxBytes, CancellationToken ct) => Task.FromResult<byte[]?>(null);
}

/// <summary>Used where the engine cannot move files through a volume: a batch then ends each task as <c>container_unavailable</c> before any session container starts.</summary>
public sealed class UnavailableSessionVolumes : ISessionVolumes
{
    private static ChargehandException Unavailable() => new(ErrorCode.ContainerUnavailable, "this deployment cannot put a task into a session's output volume or read its bundle out",
        "Set driven.runner in the profile, or install Docker, and list the session image in driven.images (docs/guide/driven.md).");

    public Task WriteTaskAsync(string runId, SessionTask task, CancellationToken ct) => throw Unavailable();

    public Task WriteSeedAsync(string runId, string bundlePath, CancellationToken ct) => throw Unavailable();

    public Task FetchOutputAsync(string runId, string directory, CancellationToken ct) => throw Unavailable();

    public Task<TaskUsage?> ReadUsageAsync(string runId, CancellationToken ct) => throw Unavailable();
}
