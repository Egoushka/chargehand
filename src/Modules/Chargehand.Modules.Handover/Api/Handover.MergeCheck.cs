using Chargehand.Modules.Runners.Api;

namespace Chargehand.Modules.Handover.Api;

/// <summary>The merge check before a push (issue #737): the branch is merged with the remote's base branch as it is now by <c>git merge-tree --write-tree</c>, which writes no working tree and
/// touches no ref but a scratch one. A conflict holds the branch back, so a branch that conflicts with its base is never pushed; its work stays in the run's bundle for a continuation to rebase.</summary>
public sealed partial class Handover
{
    /// <summary>Where the remote's base branch is fetched to in the scratch clone; never pushed.</summary>
    private const string RemoteBaseRef = "refs/chargehand/remote-base";

    /// <summary>Why the branch must not be pushed, or null: it merges cleanly, or the remote's base branch (and the fallback base of a stacked pull request) cannot be read, and then the push
    /// itself says what is wrong with the remote. Runs with the push's own environment, so it asks for no credential of its own.</summary>
    private static async Task<string?> ConflictAsync(HandoverInput input, string scratch, string commit, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        foreach (var branch in new[] { input.BaseBranch, input.FallbackBaseBranch }.OfType<string>().Distinct(StringComparer.Ordinal))
        {
            var fetch = await GitProcess.RunAsync(scratch, env, ["-c", "core.hooksPath=/dev/null", "fetch", "--quiet", "--no-tags", "--", input.RemoteUrl, $"+refs/heads/{branch}:{RemoteBaseRef}"], GitTimeout, ct);
            if (fetch.ExitCode != 0)
                continue;
            var merge = await GitProcess.RunAsync(scratch, env, ["merge-tree", "--write-tree", "--name-only", "--no-messages", RemoteBaseRef, commit], GitTimeout, ct);
            if (merge.ExitCode != 1)
                return null;
            // The first line is the tree git wrote; the conflicted paths follow it.
            var conflicted = merge.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().Take(20).ToList();
            return $"the branch conflicts with {branch} as it is on the remote now ({(conflicted.Count == 0 ? "no path named" : string.Join(", ", conflicted))}); nothing was pushed";
        }
        return null;
    }

    /// <exception cref="MergeConflictException">The branch conflicts with its base.</exception>
    private static async Task RefuseConflictAsync(HandoverInput input, string scratch, string commit, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        if (await ConflictAsync(input, scratch, commit, env, ct) is { } conflict)
            throw new MergeConflictException(conflict);
    }

    private sealed class MergeConflictException(string reason) : Exception(reason);
}
