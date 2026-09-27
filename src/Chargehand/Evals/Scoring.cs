using Chargehand.Contracts;
using Chargehand.Plans;

namespace Chargehand.Evals;

/// <summary>
/// Scores, 0 to 1 (ADR 0019). "quality" is what the gate compares; the other names are its parts, recorded for review.
/// Only a worker item with a fact checklist uses a model call, the fact judge's, made before scoring.
/// </summary>
public static class Scoring
{
    /// <summary>A repeated wrong statement costs this much quality.</summary>
    /// <remarks>ponytail: a flat penalty per statement; weigh statements when a checklist lists harmless and harmful ones.</remarks>
    public const double WrongPenalty = 0.25;

    /// <summary>
    /// Grounding (half the share of claims whose evidence resolved, half the recall of the reference files) times
    /// completeness, less <see cref="WrongPenalty"/> per known wrong statement repeated. Completeness is the share of
    /// the item's reference facts the answer states when it has a checklist, else the claims kept over the reference
    /// claim count (at most 1). The worker node enforces grounding at run time, so a thinner answer shows in what it
    /// states, not in what it cites.
    /// </summary>
    public static IReadOnlyDictionary<string, double> Worker(ResultContract result, IReadOnlyList<string> referenceFiles, int? referenceClaims = null,
        FactCheck? facts = null)
    {
        if (result.Status != ResultStatus.Completed)
            return new Dictionary<string, double> { ["quality"] = 0 };
        var resolved = Resolved(result);
        var cited = result.Evidence.Where(e => e.Kind == EvidenceKind.File).Select(e => e.Locator.Split(':')[0]).ToHashSet();
        var recall = referenceFiles.Count == 0 ? 1 : referenceFiles.Count(cited.Contains) / (double)referenceFiles.Count;
        var complete = facts is { Facts: > 0 } f ? f.Stated / (double)f.Facts
            : referenceClaims is > 0 ? Math.Min(1, result.Claims.Count / (double)referenceClaims) : 1;
        var penalty = WrongPenalty * (facts?.Repeated ?? 0);
        var scores = new Dictionary<string, double>
        {
            ["quality"] = Math.Max(0, (resolved + recall) / 2 * complete - penalty),
            ["evidence_resolved"] = resolved,
            ["reference_recall"] = recall,
            ["completeness"] = complete,
        };
        if (facts is not null)
            scores["wrong_repeated"] = facts.Repeated;
        return scores;
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
