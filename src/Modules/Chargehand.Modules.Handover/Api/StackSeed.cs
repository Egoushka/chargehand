using Chargehand.Modules.Runners.Api;
namespace Chargehand.Modules.Handover.Api;

/// <param name="TaskId">The finished task the branch came from.</param>
/// <param name="Branch">Its branch, <c>chargehand/&lt;run&gt;</c>.</param>
/// <param name="BundlePath">A file holding that branch, as its session left it.</param>
public sealed record StackInput(string TaskId, string Branch, string BundlePath);

/// <param name="Branch">The new task's branch: the bundle that comes out holds this ref.</param>
/// <param name="SourcePath">chargehand's own checkout of the base commit.</param>
/// <param name="Directory">A scratch directory for the work; it does not exist yet.</param>
/// <param name="OutBundle">Where the seed bundle is written.</param>
public sealed record StackRequest(string Branch, string SourcePath, string BaseCommit, string ScratchRoot, string Directory, string OutBundle, IReadOnlyList<StackInput> Inputs);

/// <param name="Conflicts">The paths that conflicted when the branches were merged; empty when the merge went through.</param>
/// <param name="ConflictWith">The task whose branch could not be merged in.</param>
/// <param name="Failure">Why the seed could not be made when it is not a merge conflict; null otherwise.</param>
public sealed record StackResult(bool Made, IReadOnlyList<string> Conflicts, string? ConflictWith, string? Failure, int Commits)
{
    public static StackResult Ok(int commits) => new(true, [], null, null, commits);
}

/// <summary>Builds the starting point of a task that depends on others (ADR 0049): one branch, named for the new task, that holds the work of every task it depends on, as a bundle the
/// session container is seeded with.</summary>
public interface IStackSeeder
{
    Task<StackResult> BuildAsync(StackRequest request, CancellationToken ct);
}

/// <summary>Does it with <c>git</c> on this host, outside any container, as the handover does: a scratch clone of chargehand's checkout, the dependencies' bundles fetched into it (a bundle is
/// objects and refs, no configuration, no hooks), the first one's branch checked out under the new name and every other one merged in, in the order the task lists them. A merge that conflicts
/// is aborted and its paths are reported. The new branch is then bundled against the base commit, which is what a session's own bundle holds.</summary>
public sealed class GitStackSeeder : IStackSeeder
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(5);

    private static readonly string[] Safe = ["-c", "core.hooksPath=/dev/null", "-c", "commit.gpgsign=false", "-c", "user.name=chargehand", "-c", "user.email=chargehand@localhost"];

    public async Task<StackResult> BuildAsync(StackRequest request, CancellationToken ct)
    {
        var env = GitProcess.Environment(request.ScratchRoot);
        Task<GitResult> Git(string directory, params string[] args) => GitProcess.RunAsync(directory, env, [.. Safe, .. args], GitTimeout, ct);
        StackResult Failed(string what, GitResult r) => new(false, [], null, $"{what}: {r.Tail}", 0);

        Directory.CreateDirectory(request.ScratchRoot);
        var clone = await Git(request.ScratchRoot, "clone", "--quiet", "--no-hardlinks", "--template=", "--", request.SourcePath, request.Directory);
        if (clone.ExitCode != 0)
            return Failed("chargehand could not clone its checkout", clone);
        var checkedOut = await Git(request.Directory, "checkout", "--quiet", "--detach", request.BaseCommit);
        if (checkedOut.ExitCode != 0)
            return Failed("the base commit is not in chargehand's checkout", checkedOut);

        for (var i = 0; i < request.Inputs.Count; i++)
        {
            var input = request.Inputs[i];
            var verify = await Git(request.Directory, "bundle", "verify", input.BundlePath);
            if (verify.ExitCode != 0)
                return Failed($"the branch of task '{input.TaskId}' does not verify against the base", verify);
            var fetch = await Git(request.Directory, "-c", "fetch.fsckObjects=true", "fetch", "--quiet", input.BundlePath, $"refs/heads/{input.Branch}:refs/chargehand-stack/{i}");
            if (fetch.ExitCode != 0)
                return Failed($"the bundle of task '{input.TaskId}' has no usable {input.Branch}", fetch);
        }

        var first = await Git(request.Directory, "checkout", "--quiet", "-b", request.Branch, "refs/chargehand-stack/0");
        if (first.ExitCode != 0)
            return Failed($"could not start {request.Branch} from task '{request.Inputs[0].TaskId}'", first);
        for (var i = 1; i < request.Inputs.Count; i++)
        {
            var merge = await Git(request.Directory, "merge", "--no-edit", "--no-ff", "-m", $"Merge task {request.Inputs[i].TaskId} ({request.Inputs[i].Branch})", $"refs/chargehand-stack/{i}");
            if (merge.ExitCode == 0)
                continue;
            var unmerged = await Git(request.Directory, "diff", "--name-only", "--diff-filter=U", "-z");
            List<string> paths = [.. unmerged.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
            await Git(request.Directory, "merge", "--abort");
            return paths.Count > 0 ? new StackResult(false, paths, request.Inputs[i].TaskId, null, 0) : Failed($"merging task '{request.Inputs[i].TaskId}' failed", merge);
        }

        var count = await Git(request.Directory, "rev-list", "--count", $"{request.BaseCommit}..{request.Branch}");
        var bundle = await Git(request.Directory, "bundle", "create", request.OutBundle, $"refs/heads/{request.Branch}", "--not", request.BaseCommit);
        if (bundle.ExitCode != 0)
            return Failed("could not bundle the starting branch", bundle);
        return StackResult.Ok(int.TryParse(count.Stdout.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0);
    }
}
