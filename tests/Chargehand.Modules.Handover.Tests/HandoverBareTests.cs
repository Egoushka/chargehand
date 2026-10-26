using Chargehand.Contracts;
using Chargehand.Modules.Handover.Api;

namespace Chargehand.Modules.Handover.Tests;

using Handover = Chargehand.Modules.Handover.Api.Handover;

/// <summary>The bare arm's handover (ADR 0080): the branch is scanned and pushed as it is, with no test run and no pull request.</summary>
public class HandoverBareTests
{
    private static readonly string AwsKey = "AKIA" + "IOSFODNN7EXAMPLE";

    [Fact]
    public async Task A_bare_branch_is_pushed_without_a_test_run_or_a_pull_request()
    {
        using var f = new HandoverTests.Fixture();
        var verifier = new HandoverTests.FakeVerifier(exit: 1);
        var prs = new HandoverTests.FakePullRequests();
        List<HandoverEvent> seen = [];

        var outcome = await new Handover(verifier, prs).RunAsync(f.Input(with: i => i with { Bare = true, Report = seen.Add }), default);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        Assert.Null(outcome.PullRequest);
        Assert.Null(outcome.Verification);
        Assert.Equal(outcome.Commit, f.RemoteRef("refs/heads/chargehand/run1"));
        Assert.Empty(verifier.Calls);
        Assert.Empty(prs.Created);
        Assert.Equal([RunEventKind.Pushed], seen.Select(e => e.Kind));
    }

    [Fact]
    public async Task A_bare_branch_that_carries_a_secret_is_still_refused()
    {
        using var f = new HandoverTests.Fixture(edit: w => File.WriteAllText(Path.Combine(w, "config.txt"), $"key = {AwsKey}\n"));

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), new HandoverTests.FakePullRequests()).RunAsync(f.Input(with: i => i with { Bare = true }), default);

        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Equal("", f.RemoteRef("refs/heads/chargehand/run1"));
    }

    [Fact]
    public async Task A_bare_branch_that_edits_ci_configuration_is_still_refused()
    {
        using var f = new HandoverTests.Fixture(file: ".github/workflows/ci.yml");

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), new HandoverTests.FakePullRequests()).RunAsync(f.Input(with: i => i with { Bare = true }), default);

        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Contains("CI configuration", outcome.Reason);
    }
}
