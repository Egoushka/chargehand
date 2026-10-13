using Chargehand.Containers;
using Chargehand.Contracts;
using Chargehand.Modules.Journal.Api;

namespace Chargehand.Driven;

/// <summary>The session's workspace helper as a recorded step, keyed <c>prep/&lt;run&gt;</c> in the batch's flow (ADR 0061). A task is prepared once per run id, so a key that completed never
/// runs again. A step that is run again (the server stopped or the call was cancelled between its intent and its result, or it failed) first clears what the earlier attempt left. The volume is
/// removed here: a clone does not go into a part-filled one. The helper container is not: the engine removes a stopped one under its fixed name before it runs the helper
/// (<c>DockerCliEngine.RunHelperAsync</c>, #261 and #266), on a runner as well as on a local engine, and a running one is another call for the same run, which is waited for.
/// The verification's own workspace (<see cref="ContainerVerifier"/>) is not a step: it takes the same run id after removing the volume on purpose, which a skip-if-completed key would defeat.</summary>
public sealed class JournaledWorkspaceEngine(IWorkspaceEngine inner, IContainerEngine engine, DrivenJournal steps, string batchId) : IWorkspaceEngine
{
    public Task PrepareWorkspaceAsync(WorkspaceSpec spec, CancellationToken ct) =>
        steps.RunAsync(StepKey.Prep(batchId, spec.RunId), async (attempt, token) =>
        {
            if (attempt.Repeated)
                await engine.RemoveVolumeAsync(spec.WorkVolume, token);
            await inner.PrepareWorkspaceAsync(spec, token);
        }, ct);
}

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
