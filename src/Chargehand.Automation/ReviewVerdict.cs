using Chargehand.Contracts;

namespace Chargehand.Automation;

/// <summary>What a finished review says about a pull request (ADR 0051): a finding is blocking when its confidence is at or above the threshold, and a review with no blocking finding is the approval.</summary>
public static class ReviewVerdict
{
    public static bool IsBlocking(Claim finding, decimal blockAt) => double.IsNaN(finding.Confidence) || (decimal)finding.Confidence >= blockAt;

    public static bool IsClean(IReadOnlyList<Claim> findings, decimal blockAt) => !findings.Any(f => IsBlocking(f, blockAt));
}
