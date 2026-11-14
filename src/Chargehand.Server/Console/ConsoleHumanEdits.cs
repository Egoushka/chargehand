using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Server;

/// <summary>One file's count of one <see cref="HumanEditCategory"/>, in one repository's heatmap.</summary>
public sealed record HumanEditCell(string File, string Category, int Count);

/// <summary>One repository's heatmap (issue #455): the files humans touch most often fixing a chargehand pull request, by rule category, built on the human-edit classification
/// (issue #452). <paramref name="Files"/> is the busiest files, most-touched first, capped at <see cref="ConsoleHumanEdits.MaxFiles"/>; <paramref name="Categories"/> is every
/// category in a fixed order, so the grid's columns line up across repositories even where a category has no cell.</summary>
public sealed record HumanEditRepositoryBody(string Repository, IReadOnlyList<string> Files, IReadOnlyList<string> Categories, IReadOnlyList<HumanEditCell> Cells, int Commits, int FilesTotal);

/// <summary><c>human-edits/v1</c>: per repository, which files and rule categories humans fix most after a chargehand pull request (issue #452's classification, issue #455's heatmap).</summary>
public sealed record HumanEditsBody(string ContractVersion, DateTimeOffset GeneratedAt, IReadOnlyList<HumanEditRepositoryBody> Repositories, IReadOnlyList<string> NotRecorded);

internal static class ConsoleHumanEdits
{
    public const string Contract = "human-edits/v1";

    /// <summary>The most touched files kept per repository; a long tail would crowd the heatmap more than it informs.</summary>
    public const int MaxFiles = 25;

    /// <summary>Every category, in the order the grid's columns show them; <see cref="HumanEditCategory.Other"/> last, as the catch-all it is.</summary>
    public static readonly IReadOnlyList<string> Categories =
        [HumanEditCategory.Lint, HumanEditCategory.MissingImport, HumanEditCategory.UnboundedInput, HumanEditCategory.CiConfig, HumanEditCategory.Other];

    private static readonly IReadOnlyList<string> NotRecorded =
    [
        "A commit counts once per file it changed, under the category the classifier gave the whole commit: a commit touching five files adds one to each, so a repository's commit count and the sum of its cells differ.",
        "A commit whose task has dropped out of the learning window, or whose pull request record cannot be read back, is left out: its repository cannot be determined.",
        "Classification runs on a schedule after a pull request reads back merged with edits (driven.learning.pr_check_minutes); a very recent merge may not be reflected yet.",
    ];

    public static async Task<HumanEditsBody> BuildAsync(ConsoleContext c, Principal? who, CancellationToken ct)
    {
        var learning = (await c.LearningAsync(ct)).Where(e => Principal.Sees(who, e.Record.Actor)).ToDictionary(e => e.Record.RunId, e => e, StringComparer.Ordinal);
        var edits = await c.HumanEditsAsync(ct);

        var byRepository = new Dictionary<string, List<HumanEditCommit>>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            if (!learning.TryGetValue(edit.RunId, out var entry) || entry.Record.Pr is not { } pr || ConsoleBatches.RepositoryName(pr.Remote) is not { } repository)
                continue;
            (byRepository.TryGetValue(repository, out var list) ? list : byRepository[repository] = []).Add(edit);
        }

        var repositories = byRepository.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => Grid(kv.Key, kv.Value)).ToList();
        return new HumanEditsBody(Contract, c.Now, repositories, NotRecorded);
    }

    private static HumanEditRepositoryBody Grid(string repository, IReadOnlyList<HumanEditCommit> edits)
    {
        var counts = new Dictionary<(string File, string Category), int>();
        var fileTotals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            foreach (var file in edit.ChangedPaths.Distinct(StringComparer.Ordinal))
            {
                counts[(file, edit.Category)] = counts.GetValueOrDefault((file, edit.Category)) + 1;
                fileTotals[file] = fileTotals.GetValueOrDefault(file) + 1;
            }
        }
        var files = fileTotals.OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).Take(MaxFiles).Select(f => f.Key).ToList();
        var kept = files.ToHashSet(StringComparer.Ordinal);
        var cells = counts.Where(kv => kept.Contains(kv.Key.File)).Select(kv => new HumanEditCell(kv.Key.File, kv.Key.Category, kv.Value)).ToList();
        return new HumanEditRepositoryBody(repository, files, Categories, cells, edits.Count, fileTotals.Count);
    }
}
