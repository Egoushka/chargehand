using Chargehand.Containers;
using Chargehand.Contracts;
using Chargehand.Modules.Journal.Api;

namespace Chargehand.Driven;

/// <summary>The session's workspace helper as a recorded step, keyed <c>prep/&lt;run&gt;</c> in the batch's flow (ADR 0061). A task is prepared once per run id, so a key that completed never
/// runs again. A step that is run again (the server stopped or the call was cancelled between its intent and its result, or it failed) first clears what the earlier attempt left. The volume is
/// removed here: a clone does not go into a part-filled one. The helper container is not: the engine removes a stopped one under its fixed name before it runs the helper
/// (<c>DockerCliEngine.RunHelperAsync</c>, #261 and #266), on a runner as well as on a local engine, and a running one is another call for the same run, which is waited for.
/// The verification's own workspace is another key (<see cref="JournaledVerifierWorkspace"/>). A session that ran and ended without an outcome resets this key, so its next start prepares the volume fresh.</summary>
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

/// <summary>The verification's workspace helper (<see cref="ContainerVerifier"/>) as the step <c>prep/&lt;run&gt;/verify</c> (ADR 0061). Each verification removes the work volume and prepares it
/// again under the session's run id on purpose, so a finished key is reset before each prep; one a cut left started runs again, resumed. The verifier has removed the volume either way.</summary>
public sealed class JournaledVerifierWorkspace(IWorkspaceEngine inner, DrivenJournal steps, string batchId) : IWorkspaceEngine
{
    public async Task PrepareWorkspaceAsync(WorkspaceSpec spec, CancellationToken ct)
    {
        var key = StepKey.VerifyPrep(batchId, spec.RunId);
        if (!(await steps.UnfinishedAsync(batchId, ct)).Contains(key))
            await steps.ResetAsync(key, ct);
        await steps.RunAsync(key, (_, token) => inner.PrepareWorkspaceAsync(spec, token), ct);
    }
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
