using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Driven;

/// <summary>Fills a workspace on a runner that cannot see the server's checkouts (ADR 0058): the repository bundle goes into the run's output volume as <c>source.bundle</c>, then the runner's helper clones
/// from it. The verification of a finished branch asks for a workspace again, in the same output volume, and gets the bundle sent again: the session had that volume mounted writable, and a bundle is
/// content-addressed, so what it sent cannot make the pinned commit mean anything else, but the verification does not depend on it being there.</summary>
public sealed class BundleWorkspaceEngine(IOutVolumeEngine files, IBundleWorkspaceEngine runner, RepositoryBundles bundles) : IWorkspaceEngine
{
    public async Task PrepareWorkspaceAsync(WorkspaceSpec spec, CancellationToken ct)
    {
        var path = await bundles.BundleAsync(spec.SourcePath, spec.Commit, ct);
        await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await files.WriteOutStreamAsync(new OutFileSpec(spec.RunId, spec.Image, RunnerNames.Out(spec.RunId), ContainerTemplate.SourceBundleFile), file, file.Length, ct);
        await runner.PrepareBundleWorkspaceAsync(new BundleWorkspaceSpec(spec.RunId, spec.Image, RunnerNames.Out(spec.RunId), spec.WorkVolume, spec.Branch, spec.Commit), ct);
        // The workspace has what it needs. The output volume of a finished task is kept for days for a continuation, and the repository's history is not what it is kept for: empty the file.
        try
        {
            await files.WriteOutStreamAsync(new OutFileSpec(spec.RunId, spec.Image, RunnerNames.Out(spec.RunId), ContainerTemplate.SourceBundleFile), Stream.Null, 0, ct);
        }
        catch (ChargehandException)
        {
            // Space only: a runner that did not take this keeps the file until the volume goes.
        }
    }
}
