namespace Chargehand.Modules.Oversight.Api;

/// <summary>Turns recurring human fixes into a proposal (issue #452), the way CLAUDE.md already asks of a person: "a mistake an agent makes twice becomes a line in this file".
/// <see cref="Propose"/> is pure: given the commits already classified and one more just classified, it says whether this is the occurrence that should be written up, and if so,
/// what the proposal says. It never reads a store or writes a sink itself, so a classification test can call it with fixture commits alone.</summary>
public static class HumanEditLearner
{
    /// <summary>The occurrence of a category, counting the new commit, that is written up: the second, not the first (a single fix might be a fluke) and not a later repeat (the
    /// category already has a proposal).</summary>
    public const int ProposalThreshold = 2;

    /// <returns>Null when <paramref name="latest"/> does not cross <see cref="ProposalThreshold"/> for its category, or is <see cref="HumanEditCategory.Other"/>.</returns>
    public static ProposalRecord? Propose(IReadOnlyList<HumanEditCommit> history, HumanEditCommit latest, DateTimeOffset now)
    {
        if (latest.Category == HumanEditCategory.Other)
            return null;
        var sameCategory = history.Where(h => h.Category == latest.Category).ToList();
        var occurrences = sameCategory.Count + 1;
        if (occurrences != ProposalThreshold)
            return null;
        var (title, body) = Template(latest.Category, occurrences);
        return new ProposalRecord(now, latest.Category, occurrences, title, body, [.. sameCategory.Select(c => c.PrUrl), latest.PrUrl]);
    }

    private static (string Title, string Body) Template(string category, int occurrences) => category switch
    {
        HumanEditCategory.Lint =>
            ("Run the formatter before pushing", $"{occurrences} merged pull requests needed a human lint or format fix. " +
                "Add a formatter check (dotnet format --verify-no-changes, or the project's own) to the pre-push gate."),
        HumanEditCategory.MissingImport =>
            ("Build before pushing, not just format", $"{occurrences} merged pull requests needed a human fix for a missing import or using. " +
                "Add a build step (treating warnings as errors) to the pre-push gate, ahead of any push."),
        HumanEditCategory.UnboundedInput =>
            ("Bound untrusted input before pushing", $"{occurrences} merged pull requests needed a human fix for unbounded or untrusted input. " +
                "Add a rule line to CLAUDE.md naming the input and its bound, so the next change starts from it."),
        HumanEditCategory.CiConfig =>
            ("Route CI edits through a person", $"{occurrences} merged pull requests needed a human fix to CI configuration. " +
                "Keep CI paths out of driven changes (as CLAUDE.md already asks) and open a tracker item instead of editing them directly."),
        _ => ("Repeated human fix", $"{occurrences} merged pull requests needed the same kind of human fix ({category})."),
    };
}
