using System.Globalization;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Modules.Handover.Api;

/// <summary>Issue 430: the branch's history carries no more paths than <see cref="PushGuard.MaxBranchFiles"/> and no file over <see cref="PushGuard.MaxFileBytes"/>. Scratch output is dropped before
/// this runs (<see cref="ScrubJunkAsync"/>, issue 914), so the junk check here is a backstop for a rewrite that failed, not the usual path. Every commit is pushed, so a file a later commit removed
/// is on the remote all the same.</summary>
public sealed partial class Handover
{
    /// <summary>How many refused or dropped paths a reason or note names before it says how many more there are.</summary>
    private const int NamedPaths = 10;

    /// <summary>The branch against its base: it changes something, its history carries no junk once <see cref="ScrubJunkAsync"/> has dropped any, and its diff no secret.</summary>
    private static async Task<HandoverOutcome?> ScanDiffAsync(HandoverRun run, CancellationToken ct)
    {
        await ScrubJunkAsync(run, ct);
        var (input, scratch, env, commit) = (run.Input, run.Scratch, run.Env, run.Commit);
        var names = await GitProcess.RunAsync(scratch, env, ["diff", "--name-only", "-z", input.BaseCommit, commit], GitTimeout, ct);
        run.Changed = [.. names.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        var changed = run.Changed;
        if (changed.Count == 0)
            return run.Refuse("the branch has no change against its base", ErrorCode.SessionFailed, commit: commit);
        var numstat = await GitProcess.RunAsync(scratch, env, ["diff", "--numstat", "--no-renames", "-z", input.BaseCommit, commit], GitTimeout, ct, MaxDiffBytes);
        run.Changes = new ChangeSummary(ParseNumstat(numstat.Stdout), null, false);
        if (await GuardJunkAsync(run, ct) is { } junk)
            return junk;
        var diff = await GitProcess.RunAsync(scratch, env, ["diff", "--no-color", "-U0", input.BaseCommit, commit], GitTimeout, ct, MaxDiffBytes);
        if (diff.Truncated)
            return run.Refuse("the diff is too large to scan", ErrorCode.SessionFailed, changed: changed, commit: commit);
        var findings = DiffScan.Scan(diff.Stdout, run.Secrets, changed);
        if (findings.Count > 0)
            return run.Refuse($"the diff looks like it carries a secret ({string.Join(", ", findings.Select(f => $"{f.Kind} in {f.Path}"))}); nothing was pushed", ErrorCode.SessionFailed, changed: changed, findings: findings, commit: commit);
        return null;
    }

    /// <summary>Issue 914: a scratch path (<see cref="PushGuard.Junk"/>) the branch's commits carry is dropped instead of refusing the whole run, so a session that left one beside its real change
    /// still reaches a pull request. Every commit between the base and the tip is rewritten with the junk paths removed from its tree, parents mapped the same way as <see cref="StampOneAsync"/>'s
    /// rewrite; a commit left with the same tree as its mapped parent collapses into it, so a commit that added only scratch output disappears. <see cref="HandoverRun.DroppedJunk"/> is set so the
    /// pull request body can say what was dropped. Nothing happens when the history carries no junk; the rewrite leaves the branch as it was when git fails, so <see cref="GuardJunkAsync"/> still
    /// refuses it below.</summary>
    private static async Task ScrubJunkAsync(HandoverRun run, CancellationToken ct)
    {
        var (input, scratch, env) = (run.Input, run.Scratch, run.Env);
        var range = $"{input.BaseCommit}..{run.Commit}";
        var log = await GitProcess.RunAsync(scratch, env, ["log", "-m", "--name-only", "-z", "--no-renames", "--format=", range], GitTimeout, ct, MaxDiffBytes);
        if (log.ExitCode != 0 || log.Truncated)
            return;
        var touched = log.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim('\n')).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var junk = PushGuard.FindJunk(touched).Select(j => j.Path).Distinct(StringComparer.Ordinal).ToList();
        if (junk.Count == 0)
            return;
        var list = await GitProcess.RunAsync(scratch, env, ["rev-list", "--reverse", "--topo-order", range], GitTimeout, ct);
        if (list.ExitCode != 0)
            return;
        var index = Path.Combine(input.ScratchRoot, $"{input.RunId}.scrub-index");
        Dictionary<string, string> moved = [];
        try
        {
            foreach (var commit in list.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var scrubbed = await ScrubOneAsync(scratch, env, commit.Trim(), index, junk, moved, ct);
                if (scrubbed is null)
                    return;
                moved[commit.Trim()] = scrubbed;
            }
        }
        finally
        {
            File.Delete(index);
        }
        var head = moved.GetValueOrDefault(run.Commit, run.Commit);
        if (head == run.Commit || (await GitProcess.RunAsync(scratch, env, ["update-ref", $"refs/heads/{input.Branch}", head, run.Commit], GitTimeout, ct)).ExitCode != 0)
            return;
        run.DroppedJunk = junk;
        run.Commit = head;
    }

    /// <summary>The commit with <paramref name="junk"/> dropped from its tree (via a scratch index: <c>read-tree</c>, <c>update-index --force-remove</c>, <c>write-tree</c>), parents mapped through
    /// <paramref name="moved"/>, same author, committer and message; the commit itself when neither its tree nor any parent changed. Null when git failed.</summary>
    private static async Task<string?> ScrubOneAsync(string scratch, IReadOnlyDictionary<string, string> env, string commit, string index, List<string> junk, Dictionary<string, string> moved, CancellationToken ct)
    {
        var meta = await GitProcess.RunAsync(scratch, env, ["log", "-1", "--format=%T%x00%P%x00%an%x00%ae%x00%aI%x00%cn%x00%ce%x00%cI", commit], GitTimeout, ct);
        var body = await GitProcess.RunAsync(scratch, env, ["log", "-1", "--format=%B", commit], GitTimeout, ct);
        if (meta.ExitCode != 0 || body.ExitCode != 0 || meta.Stdout.TrimEnd('\n').Split('\0') is not [var tree, var parentList, var authorName, var authorEmail, var authorDate, var committerName, var committerEmail, var committerDate])
            return null;
        var oldParents = parentList.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var parents = oldParents.Select(p => moved.GetValueOrDefault(p, p)).ToList();
        var indexEnv = new Dictionary<string, string>(env, StringComparer.Ordinal) { ["GIT_INDEX_FILE"] = index };
        if ((await GitProcess.RunAsync(scratch, indexEnv, ["read-tree", tree], GitTimeout, ct)).ExitCode != 0
            || (await GitProcess.RunAsync(scratch, indexEnv, ["update-index", "--force-remove", "--", .. junk], GitTimeout, ct)).ExitCode != 0)
            return null;
        var written = await GitProcess.RunAsync(scratch, indexEnv, ["write-tree"], GitTimeout, ct);
        if (written.ExitCode != 0)
            return null;
        var newTree = written.Stdout.Trim();
        if (newTree == tree && parents.SequenceEqual(oldParents))
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
        var made = await GitProcess.RunAsync(scratch, identity, ["commit-tree", newTree, .. parents.SelectMany(p => (string[])["-p", p]), "-m", body.Stdout.TrimEnd('\n')], GitTimeout, ct);
        return made.ExitCode == 0 ? made.Stdout.Trim() : null;
    }

    private static async Task<HandoverOutcome?> GuardJunkAsync(HandoverRun run, CancellationToken ct)
    {
        var (input, scratch, env, commit, changed) = (run.Input, run.Scratch, run.Env, run.Commit, run.Changed);
        var range = $"{input.BaseCommit}..{commit}";
        var log = await GitProcess.RunAsync(scratch, env, ["log", "-m", "--name-only", "-z", "--no-renames", "--format=", range], GitTimeout, ct, MaxDiffBytes);
        if (log.ExitCode != 0 || log.Truncated)
            return run.Refuse("chargehand could not list the paths the branch's commits touch; nothing was pushed", ErrorCode.SessionFailed, changed: changed, commit: commit);
        var touched = log.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim('\n')).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var junk = PushGuard.FindJunk(touched);
        if (junk.Count > 0)
            return run.Refuse($"the branch carries scratch output ({Named(junk.Select(j => $"{j.Kind}: {j.Path}").ToList())}); nothing was pushed", ErrorCode.SessionFailed, changed: changed, commit: commit);
        if (touched.Count > PushGuard.MaxBranchFiles)
            return run.Refuse(string.Create(CultureInfo.InvariantCulture, $"the branch's commits touch {touched.Count} files, more than {PushGuard.MaxBranchFiles}, which is generated output rather than a change; nothing was pushed"),
                ErrorCode.SessionFailed, changed: changed, commit: commit);
        var large = await LargeFilesAsync(scratch, env, range, ct);
        if (large is null)
            return run.Refuse("chargehand could not measure the files of the branch's commits; nothing was pushed", ErrorCode.SessionFailed, changed: changed, commit: commit);
        if (large.Count > 0)
            return run.Refuse($"the branch's commits carry files over {PushGuard.MaxFileBytes / 1024 / 1024} MiB ({Named(large)}); nothing was pushed", ErrorCode.SessionFailed, changed: changed, commit: commit);
        return null;
    }

    /// <summary>The paths of the blobs over <see cref="PushGuard.MaxFileBytes"/> that the commits of <paramref name="range"/> add: <c>rev-list --filter=blob:limit</c> prints the blobs it
    /// leaves out (at least the limit) with a <c>~</c>, and a second listing names them. Null when git fails.</summary>
    private static async Task<List<string>?> LargeFilesAsync(string scratch, IReadOnlyDictionary<string, string> env, string range, CancellationToken ct)
    {
        var limit = (PushGuard.MaxFileBytes + 1).ToString(CultureInfo.InvariantCulture);
        var omitted = await GitProcess.RunAsync(scratch, env, ["rev-list", "--objects", $"--filter=blob:limit={limit}", "--filter-print-omitted", "--no-object-names", range], GitTimeout, ct, MaxDiffBytes);
        if (omitted.ExitCode != 0 || omitted.Truncated)
            return null;
        var big = omitted.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith('~')).Select(l => l[1..].Trim()).ToHashSet(StringComparer.Ordinal);
        if (big.Count == 0)
            return [];
        var named = await GitProcess.RunAsync(scratch, env, ["rev-list", "--objects", range], GitTimeout, ct, MaxDiffBytes);
        if (named.ExitCode != 0 || named.Truncated)
            return null;
        return [.. named.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ', 2)).Where(p => p.Length == 2 && big.Contains(p[0])).Select(p => p[1]).Distinct(StringComparer.Ordinal)];
    }

    private static string Named(List<string> items) =>
        items.Count <= NamedPaths ? string.Join(", ", items) : string.Create(CultureInfo.InvariantCulture, $"{string.Join(", ", items.Take(NamedPaths))} and {items.Count - NamedPaths} more");
}
