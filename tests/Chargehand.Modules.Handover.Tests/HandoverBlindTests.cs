using Chargehand.Contracts;
using Chargehand.Modules.Handover.Api;

namespace Chargehand.Modules.Handover.Tests;

using Handover = Chargehand.Modules.Handover.Api.Handover;

/// <summary>Issue 789: the implementer's branch has to pass the blind tests; a failure is refused so the session can continue, and after the continuations it is kept as partial.</summary>
public class HandoverBlindTests
{
    private sealed class BlindVerifier(BlindTestResult blind) : IBranchVerifier
    {
        public Task<VerificationRecord> VerifyAsync(BranchVerification v, CancellationToken ct) =>
            Task.FromResult(new VerificationRecord(v.Argv, "detected", 0, "output", false, TimeSpan.FromSeconds(3), false, BlindTests: blind));
    }

    private static readonly BlindTestResult Failing = new(2, 1, ["DiscountTests.Applies_at_exactly_100"], []);

    [Fact]
    public async Task A_failing_blind_test_is_refused_with_its_name_so_the_session_can_continue()
    {
        using var f = new HandoverTests.Fixture(file: "src/Retry.cs");
        var prs = new HandoverTests.FakePullRequests();

        var outcome = await new Handover(new BlindVerifier(Failing), prs).RunAsync(f.Input(), default);

        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Equal(ErrorCode.VerificationFailed, outcome.Error);
        Assert.Contains("blind_tests_failed", outcome.Reason);
        Assert.Contains("DiscountTests.Applies_at_exactly_100", outcome.Reason);
        Assert.Equal(1, outcome.Verification!.BlindTests!.Failed);
        Assert.Empty(prs.Created);
        Assert.Equal("", f.RemoteRef("refs/heads/chargehand/run1"));
    }

    [Fact]
    public async Task A_partial_hand_over_keeps_the_work_and_names_the_failing_blind_tests_in_the_pull_request_body()
    {
        using var f = new HandoverTests.Fixture(file: "src/Retry.cs");
        var prs = new HandoverTests.FakePullRequests();

        var outcome = await new Handover(new BlindVerifier(Failing), prs).RunAsync(f.Input(with: i => i with { Partial = true }), default);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        var body = Assert.Single(prs.Created).Body;
        Assert.Contains("DiscountTests.Applies_at_exactly_100", body);
        Assert.Contains("Blind tests", body);
    }

    [Fact]
    public async Task Blind_tests_that_do_not_compile_on_the_base_are_reported_and_do_not_fail_the_implementer()
    {
        using var f = new HandoverTests.Fixture(file: "src/Retry.cs");
        var prs = new HandoverTests.FakePullRequests();
        var blind = new BlindTestResult(0, 0, [], ["chargehand/run1.blind"]);

        var outcome = await new Handover(new BlindVerifier(blind), prs).RunAsync(f.Input(), default);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        var body = Assert.Single(prs.Created).Body;
        Assert.Contains("blind_uncompilable", body);
        Assert.Contains("chargehand/run1.blind", body);
    }
}
