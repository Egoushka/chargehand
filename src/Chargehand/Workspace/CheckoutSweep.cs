namespace Chargehand.Workspace;

/// <summary>
/// Deletes the oldest clones under <c>worker_root/.checkouts/&lt;repository&gt;/&lt;commit&gt;</c> (issue #509): a clone per (repository, commit) is
/// reused and otherwise never deleted (<see cref="Chargehand.Orchestrator"/>'s <c>Checkout</c>, ADR 0015), and PR review makes one per head commit. Mirrors
/// <see cref="Chargehand.Modules.Batches.Api.BatchRecords.Prune"/>: past <see cref="MaxCheckouts"/>, or idle longer than <see cref="MaxAge"/>, oldest first.
/// Run once, at start, before any run can make or read a checkout, so none is ever in use when this runs.
/// </summary>
public static class CheckoutSweep
{
    public const int MaxCheckouts = 500;

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(180);

    /// <summary>Deletes the oldest clones under <paramref name="workerRoot"/>/.checkouts: past <see cref="MaxCheckouts"/>, or idle longer than <see cref="MaxAge"/>. A repository
    /// directory left with no commit under it is removed too.</summary>
    /// <returns>The clone directories removed.</returns>
    public static IReadOnlyList<string> Prune(string workerRoot, TimeProvider? clock = null)
    {
        var root = Path.Combine(Path.GetFullPath(workerRoot), ".checkouts");
        if (!Directory.Exists(root))
            return [];
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var found = new DirectoryInfo(root).EnumerateDirectories()
            .SelectMany(repo => repo.EnumerateDirectories())
            .Select(d => (d.FullName, Written: LastWrite(d)))
            .OrderByDescending(d => d.Written)
            .ToList();
        var doomed = found.Where((d, i) => i >= MaxCheckouts || now - d.Written > MaxAge).ToList();
        List<string> removed = [];
        foreach (var d in doomed)
        {
            try
            {
                Directory.Delete(d.FullName, recursive: true);
                removed.Add(d.FullName);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A clone the filesystem will not let go of is pruned at the next start.
                continue;
            }
            var parent = Path.GetDirectoryName(d.FullName)!;
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                TryDeleteEmpty(parent);
        }
        return removed;
    }

    private static void TryDeleteEmpty(string directory)
    {
        try { Directory.Delete(directory); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The idle time of a clone: the newest write among its top-level files, or the directory's own if it has none (as <see cref="Chargehand.Modules.Batches.Api.BatchRecords"/> reads a batch's age).</summary>
    private static DateTimeOffset LastWrite(DirectoryInfo d)
    {
        var files = d.EnumerateFiles().Select(f => f.LastWriteTimeUtc).ToList();
        return new DateTimeOffset(files.Count == 0 ? d.LastWriteTimeUtc : files.Max(), TimeSpan.Zero);
    }
}
