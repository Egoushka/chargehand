using Chargehand.Contracts;
using Chargehand.Modules.Handover.Api;

namespace Chargehand.Modules.Handover.Tests;

using Handover = Chargehand.Modules.Handover.Api.Handover;

/// <summary>Issue 789: a blind task's branch is tests only; any other path fails it as <c>blind_not_tests</c>.</summary>
public class BlindTestsOutputTests
{
    [Fact]
    public async Task A_blind_bundle_that_touches_a_production_file_fails_as_blind_not_tests()
    {
        using var f = new HandoverTests.Fixture(file: "src/Retry.cs");

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), new HandoverTests.FakePullRequests()).RunAsync(f.Input(with: i => i with { Bare = true, BlindTask = true }), default);

        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Contains("blind_not_tests", outcome.Reason);
        Assert.Contains("src/Retry.cs", outcome.Reason);
        Assert.Equal("", f.RemoteRef("refs/heads/chargehand/run1"));
    }

    [Fact]
    public async Task A_blind_bundle_of_test_files_only_is_pushed_without_a_pull_request()
    {
        using var f = new HandoverTests.Fixture(file: "tests/RetryTests.cs");
        var prs = new HandoverTests.FakePullRequests();

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), prs).RunAsync(f.Input(with: i => i with { Bare = true, BlindTask = true }), default);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        Assert.Empty(prs.Created);
    }

    [Fact]
    public async Task A_task_that_is_not_blind_may_touch_production_files()
    {
        using var f = new HandoverTests.Fixture(file: "src/Retry.cs");

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), new HandoverTests.FakePullRequests()).RunAsync(f.Input(with: i => i with { Bare = true }), default);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
    }
}
