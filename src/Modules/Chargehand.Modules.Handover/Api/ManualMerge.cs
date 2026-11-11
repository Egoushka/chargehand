using Chargehand.Modules.Forge.Api;

namespace Chargehand.Modules.Handover.Api;

/// <summary>Whether a person may merge a pull request from the console (ADR 0046). The integrator's mode is the person's licence here: <c>Off</c> refuses; <c>Ask</c> (which is also what
/// <c>always_ask</c> resolves to) and <c>Merge</c> allow it, because both leave the merge to a person or to the rules. The forge's own rules still hold, so the merge needs a required check
/// that passed and a state the forge calls clean. Pure: the caller reads the pull request and the base branch's rules.</summary>
public static class ManualMerge
{
    /// <summary>Why the merge is refused, in words for the person; null when it may go ahead.</summary>
    public static string? Refusal(IntegrationMode mode, ChecksGate gate, PrSnapshot pr)
    {
        if (mode == IntegrationMode.Off)
            return "the integrator is off for this repository, so chargehand does not merge here";
        if (pr.Merged)
            return "the pull request is merged already";
        if (pr.Closed)
            return "the pull request is closed";
        if (pr.IsDraft)
            return "the pull request is still a draft";
        var required = pr.Checks.Where(c => c.Required).ToList();
        if (gate != ChecksGate.Required || required.Count == 0)
            return "there is no required check to wait for (the base branch has none, or it could not be read), so the forge would merge before CI ran";
        var unfinished = required.Where(c => c.Outcome != CheckOutcome.Passed).Select(c => c.Name).Distinct().Order(StringComparer.Ordinal).ToList();
        if (unfinished.Count > 0)
            return $"required checks have not passed: {string.Join(", ", unfinished)}";
        return pr.MergeStatus switch
        {
            "CLEAN" => null,
            "DIRTY" => "the pull request conflicts with its base branch",
            var other => $"the forge says the pull request is not mergeable ({other.ToLowerInvariant()})",
        };
    }
}
