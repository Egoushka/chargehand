using Chargehand.Modules.Journal.Api;

namespace Chargehand.Modules.Sessions.Api;

/// <summary>The writes into a run's output volume as recorded steps, keyed <c>xfer/&lt;run&gt;/task</c> and <c>xfer/&lt;run&gt;/seed</c> (ADR 0061); the reads pass through. A read copies into a
/// directory this process owns and is lost with it, so it must run again after a restart. A write overwrites the same file, so running it again is safe, and the engine clears a stopped
/// transfer helper under the run's name before it runs one (see <see cref="JournaledWorkspaceEngine"/>): a resumed write needs nothing more.</summary>
public sealed class JournaledSessionVolumes(ISessionVolumes inner, DrivenJournal steps, string batchId) : ISessionVolumes
{
    public Task WriteTaskAsync(string runId, SessionTask task, CancellationToken ct) =>
        steps.RunAsync(StepKey.Transfer(batchId, runId, "task"), (_, token) => inner.WriteTaskAsync(runId, task, token), ct);

    public Task WriteSeedAsync(string runId, string bundlePath, CancellationToken ct) =>
        steps.RunAsync(StepKey.Transfer(batchId, runId, "seed"), (_, token) => inner.WriteSeedAsync(runId, bundlePath, token), ct);

    public Task FetchOutputAsync(string runId, string directory, CancellationToken ct) => inner.FetchOutputAsync(runId, directory, ct);

    public Task<TaskUsage?> ReadUsageAsync(string runId, CancellationToken ct) => inner.ReadUsageAsync(runId, ct);

    public Task<byte[]?> ReadTranscriptTailAsync(string runId, int maxBytes, CancellationToken ct) => inner.ReadTranscriptTailAsync(runId, maxBytes, ct);
}
