using System.Globalization;
using Chargehand.Contracts;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Modules.Handover.Api;

/// <summary>Issue 430: the branch's history carries no scratch output, no more paths than <see cref="PushGuard.MaxBranchFiles"/> and no file over <see cref="PushGuard.MaxFileBytes"/>.
/// Every commit is pushed, so a file a later commit removed is on the remote all the same.</summary>
public sealed partial class Handover
{
    /// <summary>How many refused paths a reason names before it says how many more there are.</summary>
    private const int NamedPaths = 10;

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
