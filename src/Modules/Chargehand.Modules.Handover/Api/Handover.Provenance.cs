using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Modules.Handover.Api;

/// <summary>The trailers that say which run wrote each commit (ADR 0070), added in the scratch clone after the tests pass and before the push.</summary>
public sealed partial class Handover
{
    /// <summary>
    /// Rewrites the messages of the commits past the base that have no <c>Chargehand-Run</c> trailer yet, with <c>git commit-tree</c>: same tree, same author and committer (name, address, date),
    /// parents mapped, so the rewrite is the same commits on every call and a repeated push finds its commit on the remote. A commit that has the trailer (an earlier session's, fetched back
    /// from the remote) is kept as it is. The branch moves to the new head; the tests ran on the bundle's trees, which are the same.
    /// </summary>
    private static async Task<HandoverOutcome?> StampAsync(HandoverRun run, CancellationToken ct)
    {
        if (run.Input.Provenance is not { } provenance)
            return null;
        var (input, scratch, env) = (run.Input, run.Scratch, run.Env);
        var list = await GitProcess.RunAsync(scratch, env, ["rev-list", "--reverse", "--topo-order", $"{input.BaseCommit}..{run.Commit}"], GitTimeout, ct);
        if (list.ExitCode != 0)
            return run.Refuse($"chargehand could not list the branch's commits: {list.Tail}", ErrorCode.SessionFailed, run.Verification, run.Changed, commit: run.Commit);
        Dictionary<string, string> moved = [];
        foreach (var commit in list.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var stamped = await StampOneAsync(scratch, env, commit.Trim(), moved, provenance, ct);
            if (stamped is null)
                return run.Refuse("chargehand could not add the run's trailers to a commit; nothing was pushed", ErrorCode.SessionFailed, run.Verification, run.Changed, commit: run.Commit);
            moved[commit.Trim()] = stamped;
        }
        var head = moved.GetValueOrDefault(run.Commit, run.Commit);
        if (head == run.Commit)
            return null;
        var sameTree = (await GitProcess.RunAsync(scratch, env, ["rev-parse", $"{head}^{{tree}}", $"{run.Commit}^{{tree}}"], GitTimeout, ct)).Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (sameTree.Length != 2 || sameTree[0] != sameTree[1]
            || (await GitProcess.RunAsync(scratch, env, ["update-ref", $"refs/heads/{input.Branch}", head, run.Commit], GitTimeout, ct)).ExitCode != 0)
            return run.Refuse("the commits with the run's trailers do not have the branch's tree; nothing was pushed", ErrorCode.SessionFailed, run.Verification, run.Changed, commit: run.Commit);
        run.Commit = head;
        return null;
    }

    /// <summary>The commit with the trailers; <paramref name="commit"/> itself when it needs none and none of its parents moved; null when git failed.</summary>
    private static async Task<string?> StampOneAsync(string scratch, IReadOnlyDictionary<string, string> env, string commit, Dictionary<string, string> moved, Provenance provenance, CancellationToken ct)
    {
        var meta = await GitProcess.RunAsync(scratch, env, ["log", "-1", "--format=%T%x00%P%x00%an%x00%ae%x00%aI%x00%cn%x00%ce%x00%cI", commit], GitTimeout, ct);
        var body = await GitProcess.RunAsync(scratch, env, ["log", "-1", "--format=%B", commit], GitTimeout, ct);
        if (meta.ExitCode != 0 || body.ExitCode != 0 || meta.Stdout.TrimEnd('\n').Split('\0') is not [var tree, var parentList, var authorName, var authorEmail, var authorDate, var committerName, var committerEmail, var committerDate])
            return null;
        var parents = parentList.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => moved.GetValueOrDefault(p, p)).ToList();
        var message = Provenance.Apply(body.Stdout.TrimEnd('\n'), provenance);
        if (message == body.Stdout.TrimEnd('\n') + "\n" && parents.SequenceEqual(parentList.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            return commit;
        Dictionary<string, string> identity = new(env, StringComparer.Ordinal)
        {
            ["GIT_AUTHOR_NAME"] = authorName,
            ["GIT_AUTHOR_EMAIL"] = authorEmail,
            ["GIT_AUTHOR_DATE"] = authorDate,
            ["GIT_COMMITTER_NAME"] = committerName,
            ["GIT_COMMITTER_EMAIL"] = committerEmail,
            ["GIT_COMMITTER_DATE"] = committerDate,
        };
        var made = await GitProcess.RunAsync(scratch, identity, ["commit-tree", tree, .. parents.SelectMany(p => (string[])["-p", p]), "-m", message.TrimEnd('\n')], GitTimeout, ct);
        return made.ExitCode == 0 ? made.Stdout.Trim() : null;
    }
}
