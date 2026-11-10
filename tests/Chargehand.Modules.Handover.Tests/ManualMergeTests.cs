using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;

namespace Chargehand.Modules.Handover.Tests;

/// <summary>When a person's merge from the console is allowed: it goes through the repository's required checks like every merge, and an integrator that is off for the repository keeps its hands off.</summary>
public class ManualMergeTests
{
    private static readonly CheckStatus Green = new("build", true, CheckOutcome.Passed, 1);

    private static PrSnapshot Pull(string status = "CLEAN", bool draft = false, bool merged = false, bool closed = false, params CheckStatus[] checks) =>
        new("PR_node", 7, "aaaaaaa1", "main", draft, merged, closed, status, false, checks.Length == 0 ? [Green] : checks, []);

    [Theory]
    [InlineData(IntegrationMode.Ask)]
    [InlineData(IntegrationMode.Merge)]
    public void A_clean_pull_request_with_every_required_check_passed_may_be_merged_in_ask_and_merge_mode(IntegrationMode mode) =>
        Assert.Null(ManualMerge.Refusal(mode, ChecksGate.Required, Pull()));

    [Fact]
    public void An_unchecked_failing_or_pending_required_check_refuses_and_an_optional_one_does_not()
    {
        Assert.Contains("no required check", ManualMerge.Refusal(IntegrationMode.Ask, ChecksGate.None, Pull()), StringComparison.Ordinal);
        Assert.Contains("no required check", ManualMerge.Refusal(IntegrationMode.Ask, ChecksGate.Required, Pull(checks: new CheckStatus("lint", false, CheckOutcome.Passed, 2))), StringComparison.Ordinal);
        Assert.Contains("test", ManualMerge.Refusal(IntegrationMode.Ask, ChecksGate.Required, Pull(checks: [Green, new CheckStatus("test", true, CheckOutcome.Pending, 3)])), StringComparison.Ordinal);
        Assert.Null(ManualMerge.Refusal(IntegrationMode.Ask, ChecksGate.Required, Pull(checks: [Green, new CheckStatus("lint", false, CheckOutcome.Failed, 4)])));
    }

    [Theory]
    [InlineData("BLOCKED", false, false, false, "not mergeable")]
    [InlineData("DIRTY", false, false, false, "conflicts")]
    [InlineData("CLEAN", true, false, false, "draft")]
    [InlineData("CLEAN", false, true, false, "merged")]
    [InlineData("CLEAN", false, false, true, "closed")]
    public void A_pull_request_the_forge_would_refuse_says_why(string status, bool draft, bool merged, bool closed, string word) =>
        Assert.Contains(word, ManualMerge.Refusal(IntegrationMode.Ask, ChecksGate.Required, Pull(status, draft, merged, closed)), StringComparison.Ordinal);

    [Fact]
    public void A_repository_the_integrator_is_off_for_is_not_merged() =>
        Assert.Contains("off", ManualMerge.Refusal(IntegrationMode.Off, ChecksGate.Required, Pull()), StringComparison.Ordinal);
}
