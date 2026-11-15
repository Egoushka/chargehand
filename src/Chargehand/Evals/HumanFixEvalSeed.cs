using Chargehand.Contracts;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Evals;

/// <summary>
/// Issue #453: each commit <see cref="HumanEditClassifier"/> classified (issue #452) becomes one item for the cell
/// whose step should have caught it, so <c>review/worker</c>, <c>code/writer</c> and <c>driven/session</c> stop
/// running with 0 items. Pure, like <see cref="HumanEditClassifier.Classify"/>: a fixture commit needs no run, no
/// forge and no checkout to become an item, because <see cref="HumanEditCommit"/> already carries everything it
/// classified on (the category and the message) and nothing it did not (no diff, no changed paths).
/// </summary>
public static class HumanFixEvalSeed
{
    /// <summary>The cell whose step should have caught each category, and the one thing a run that caught it would
    /// have said: <c>code/writer</c> runs the formatter and the build before a push (lint, a missing import);
    /// <c>review/worker</c> reads the diff for what a build cannot catch (unbounded or untrusted input);
    /// <c>driven/session</c> is the one place CLAUDE.md already asks to leave CI paths to a person.
    /// <see cref="HumanEditCategory.Other"/> has no lesson and seeds nothing, the way
    /// <see cref="HumanEditLearner.Propose"/> already leaves it unproposed.</summary>
    private static readonly Dictionary<string, (string Cell, string Fact)> ByCategory = new()
    {
        [HumanEditCategory.Lint] = ("code/writer", "Running the formatter before the change is committed would have caught this."),
        [HumanEditCategory.MissingImport] = ("code/writer", "Building the change, not just formatting it, before it is pushed would have caught this."),
        [HumanEditCategory.UnboundedInput] = ("review/worker", "The diff leaves an input unbounded or untrusted; a complete review names it and where it needs a bound."),
        [HumanEditCategory.CiConfig] = ("driven/session", "The change edits CI configuration, which a driven session must leave to a person instead of pushing itself."),
    };

    /// <summary>One item per classified commit whose category has a lesson, for the cell <see cref="ByCategory"/> names if the caller's cells still carry it (a cell renamed or
    /// dropped from <c>evals/cells.json</c> seeds nothing for its category rather than guessing a dataset). <paramref name="repoPath"/> pins the item's <c>context.repository</c>
    /// to the checkout the commit's sha belongs to (this repository, since issue #452's commits are chargehand's own).</summary>
    public static IEnumerable<EvalItem> Seed(IReadOnlyList<EvalCell> cells, IReadOnlyList<HumanEditCommit> commits, string repoPath)
    {
        foreach (var commit in commits)
        {
            if (!ByCategory.TryGetValue(commit.Category, out var lesson))
                continue;
            if (cells.FirstOrDefault(c => c.Name == lesson.Cell) is not { Preset: { } preset } cell)
                continue;
            var request = new RunRequest("request/v1", Question(commit.Category), new RequestContext(false, preset, Repository: new RepositoryRef(repoPath, commit.Sha)),
                [new CallerInput(commit.Sha, "commit", commit.Message, Uri.IsWellFormedUriString(commit.PrUrl, UriKind.Absolute) ? commit.PrUrl : null)]);
            yield return new EvalItem($"human-fix-{commit.Sha}", request, new EvalExpected(Facts: [lesson.Fact]), new Dictionary<string, string>
            {
                ["source"] = "human-fix",
                ["category"] = commit.Category,
                ["run_id"] = commit.RunId,
                ["pr_url"] = commit.PrUrl,
                ["cell"] = cell.Name,
            });
        }
    }

    private static string Question(string category) => category switch
    {
        HumanEditCategory.Lint or HumanEditCategory.MissingImport =>
            "A person added the input commit to this pull request's branch after it was pushed. What in the change should have been caught before it was pushed, and how?",
        HumanEditCategory.UnboundedInput =>
            "A person added the input commit to this pull request's branch after it was pushed to bound an input the change had left open. Review the change the commit fixes: " +
            "what should a review have said about that input?",
        HumanEditCategory.CiConfig =>
            "A person added the input commit to this pull request's branch after it was pushed, to a CI configuration path a driven session pushed to directly. Which rule does that break?",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "no lesson for this category"),
    };
}
