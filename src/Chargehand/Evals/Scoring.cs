using Chargehand.Contracts;
using Chargehand.Plans;
using Chargehand.Verification;

namespace Chargehand.Evals;

/// <summary>
/// Deterministic scores, 0 to 1, no model call (ADR 0019). "quality" is what the gate compares; the other names are
/// its parts, recorded for review.
/// </summary>
public static class Scoring
{
    /// <summary>
    /// Half the share of claims whose evidence resolved, half the recall of the reference line ranges: those a cited
    /// range in the same file overlaps. An item without ranges falls back to the recall of the reference files.
    /// </summary>
    public static IReadOnlyDictionary<string, double> Worker(ResultContract result, EvalExpected expected)
    {
        if (result.Status != ResultStatus.Completed)
            return new Dictionary<string, double> { ["quality"] = 0 };
        var resolved = Resolved(result);
        var locators = result.Evidence.Where(e => e.Kind == EvidenceKind.File).Select(e => e.Locator).ToList();
        var files = expected.ReferenceFiles ?? [];
        var cited = locators.Select(l => l.Split(':')[0]).ToHashSet();
        var recall = files.Count == 0 ? 1 : files.Count(cited.Contains) / (double)files.Count;
        var scores = new Dictionary<string, double> { ["quality"] = (resolved + recall) / 2, ["evidence_resolved"] = resolved, ["reference_recall"] = recall };
        if (expected.ReferenceRanges is not { Count: > 0 } ranges)
            return scores;
        // ponytail: any overlap counts, so one wide range covers every reference in its file; weigh by lines covered
        // if answers start citing whole files.
        var spans = Ranges(locators).ToList();
        var reference = Ranges(ranges).ToList();
        var rangeRecall = reference.Count(r => spans.Any(c => c.Path == r.Path && c.Start <= r.End && c.End >= r.Start)) / (double)reference.Count;
        scores["range_recall"] = rangeRecall;
        scores["quality"] = (resolved + rangeRecall) / 2;
        return scores;
    }

    private static IEnumerable<(string Path, int Start, int End)> Ranges(IEnumerable<string> locators)
    {
        foreach (var l in locators)
            if (GitEvidenceResolver.TryParseRange(l, out var path, out var start, out var end))
                yield return (path, start, end);
    }

    /// <summary>Mean of: a draft within bounds, claims whose evidence resolved, required inputs cited, no banned phrase.</summary>
    public static IReadOnlyDictionary<string, double> Draft(ResultContract result, DraftExpectation expected)
    {
        var draft = result.Artifacts.FirstOrDefault(a => a.Kind == "draft" && a.Content is not null)?.Content;
        if (result.Status != ResultStatus.Completed || draft is null)
            return new Dictionary<string, double> { ["quality"] = 0 };
        var within = draft.Length <= expected.MaxChars ? 1.0 : 0.0;
        var resolved = Resolved(result);
        var inputs = result.Evidence.Where(e => e.Kind == EvidenceKind.Input).Select(e => e.Locator).ToHashSet();
        var required = expected.RequiredInputs is { Count: > 0 } r ? r.Count(inputs.Contains) / (double)r.Count : 1;
        var clean = (expected.Banned ?? []).Any(b => draft.Contains(b, StringComparison.OrdinalIgnoreCase)) ? 0.0 : 1.0;
        return new Dictionary<string, double>
        {
            ["quality"] = (within + resolved + required + clean) / 4,
            ["within_bounds"] = within,
            ["evidence_resolved"] = resolved,
            ["required_inputs"] = required,
            ["no_banned_phrase"] = clean,
        };
    }

    /// <summary>1 when intake chose the expected action (a split must also pass the plan checks), else 0.</summary>
    public static IReadOnlyDictionary<string, double> Intake(TaskSpec? spec, string expectedAction)
    {
        var action = spec?.Action.ToString().ToLowerInvariant() ?? "needs_input";
        var ok = action == expectedAction && (spec?.Action != TaskAction.Split || SplitPlan.From(spec).Nodes is not null);
        return new Dictionary<string, double> { ["quality"] = ok ? 1 : 0 };
    }

    /// <summary>Claims kept over claims kept plus claims moved to open questions as unverified (ResultAssembler).</summary>
    private static double Resolved(ResultContract result)
    {
        var unverified = result.OpenQuestions.Count(q => q.StartsWith("Unverified: ", StringComparison.Ordinal) || q.Contains("] Unverified: ", StringComparison.Ordinal));
        var total = result.Claims.Count + unverified;
        return total == 0 ? 0 : result.Claims.Count / (double)total;
    }
}
