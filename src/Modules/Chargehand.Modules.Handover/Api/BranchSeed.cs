using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Runners.Api;
namespace Chargehand.Modules.Handover.Api;

/// <param name="Branch">The branch on the remote, <c>chargehand/&lt;run&gt;</c>; the bundle that comes out holds this ref.</param>
/// <param name="SourcePath">chargehand's own checkout of the base commit.</param>
/// <param name="RemoteUrl">The remote the branch is fetched from, with <paramref name="Credential"/> (the same one the handover pushes with).</param>
/// <param name="Directory">A scratch directory for the work; it does not exist yet.</param>
/// <param name="OutBundle">Where the seed bundle is written.</param>
public sealed record BranchSeedRequest(string Branch, string SourcePath, string BaseCommit, string RemoteUrl, PushCredential? Credential, string ScratchRoot, string Directory, string OutBundle, bool AllowLocalRemote = false);

/// <param name="Failure">Why the seed could not be made; null when it was.</param>
public sealed record BranchSeedResult(bool Made, string? Failure, int Commits)
{
    public static BranchSeedResult Ok(int commits) => new(true, null, commits);
}

/// <summary>Builds the starting point of a review round (ADR 0051): the finished task's branch as the remote has it now, as a bundle the session container is seeded with.</summary>
public interface IBranchSeeder
{
    Task<BranchSeedResult> BuildAsync(BranchSeedRequest request, CancellationToken ct);
}

/// <summary>Does it with <c>git</c> on this host, outside any container, as the handover does: a scratch clone of chargehand's checkout at the base commit, the branch fetched into it from the remote with the
/// push credential (never in a container), checked to descend from the base commit, and bundled against it, which is what a session's own bundle holds.</summary>
public sealed class GitBranchSeeder : IBranchSeeder
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(5);

    public async Task<BranchSeedResult> BuildAsync(BranchSeedRequest request, CancellationToken ct)
    {
        if (!request.Branch.StartsWith("chargehand/", StringComparison.Ordinal) || request.Branch.Contains("..", StringComparison.Ordinal) || request.Branch.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-')))
            return new(false, "the branch is not a chargehand branch", 0);
        if (!Handover.RemoteAllowed(request.RemoteUrl, request.AllowLocalRemote))
            return new(false, "the remote is not an https or ssh remote", 0);
        var env = GitProcess.Environment(request.ScratchRoot);
        PushCredential? credential;
        try
        {
            credential = request.Credential is { } stored ? await stored.CurrentAsync(ct) : null;
        }
        catch (GitHubAppException e)
        {
            return new(false, $"no credential for the remote: {e.Message}", 0);
        }
        var remoteEnv = Handover.PushEnvironment(credential, request.RemoteUrl, request.ScratchRoot, env);
        BranchSeedResult Failed(string what, GitResult r) => new(false, $"{what}: {r.Tail}", 0);

        Directory.CreateDirectory(request.ScratchRoot);
        var clone = await GitProcess.RunAsync(request.ScratchRoot, env, ["clone", "--quiet", "--no-hardlinks", "--template=", "--", request.SourcePath, request.Directory], GitTimeout, ct);
        if (clone.ExitCode != 0)
            return Failed("chargehand could not clone its checkout", clone);
        var checkedOut = await GitProcess.RunAsync(request.Directory, env, ["checkout", "--quiet", "--detach", request.BaseCommit], GitTimeout, ct);
        if (checkedOut.ExitCode != 0)
            return Failed("the base commit is not in chargehand's checkout", checkedOut);
        var fetch = await GitProcess.RunAsync(request.Directory, remoteEnv, ["-c", "fetch.fsckObjects=true", "-c", "core.hooksPath=/dev/null", "fetch", "--quiet", request.RemoteUrl, $"refs/heads/{request.Branch}:refs/heads/{request.Branch}"], GitTimeout, ct);
        if (fetch.ExitCode != 0)
            return Failed($"the remote has no usable {request.Branch}", fetch);
        var ancestor = await GitProcess.RunAsync(request.Directory, env, ["merge-base", "--is-ancestor", request.BaseCommit, $"refs/heads/{request.Branch}"], GitTimeout, ct);
        if (ancestor.ExitCode != 0)
            return new(false, "the branch on the remote no longer descends from the commit the task started at", 0);
        var count = await GitProcess.RunAsync(request.Directory, env, ["rev-list", "--count", $"{request.BaseCommit}..refs/heads/{request.Branch}"], GitTimeout, ct);
        var bundle = await GitProcess.RunAsync(request.Directory, env, ["bundle", "create", request.OutBundle, $"refs/heads/{request.Branch}", "--not", request.BaseCommit], GitTimeout, ct);
        if (bundle.ExitCode != 0)
            return Failed("could not bundle the branch", bundle);
        return BranchSeedResult.Ok(int.TryParse(count.Stdout.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0);
    }
}
