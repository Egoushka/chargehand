using Chargehand.Contracts;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;

namespace Chargehand.Modules.Handover.Tests;

using Handover = Chargehand.Modules.Handover.Api.Handover;

/// <summary>ADR 0078, step 4: with <c>driven.environment: "mise"</c> the repository's CI verifies. The hand-over runs no fresh-container verification, and the pull request says
/// what verifies it and that the session driver's pre-push check is a filter, not proof.</summary>
public class HandoverCiVerifiesTests
{
    private sealed class RulesPullRequests(ChecksGate gate) : IPullRequests, IBranchRules
    {
        public HandoverTests.FakePullRequests Inner { get; } = new();

        public Task<ChecksGate> ChecksGateAsync(RemoteRepository repository, string branch, CancellationToken ct) => Task.FromResult(gate);

        public Task<PullRequestRef> CreateDraftAsync(RemoteRepository repo, string head, string @base, string title, string body, CancellationToken ct) => Inner.CreateDraftAsync(repo, head, @base, title, body, ct);

        public Task EditAsync(RemoteRepository repo, int number, string title, string body, CancellationToken ct) => Inner.EditAsync(repo, number, title, body, ct);

        public Task<PullRequestRef?> FindByHeadAsync(RemoteRepository repo, string head, CancellationToken ct) => Inner.FindByHeadAsync(repo, head, ct);
    }

    private static void Ci(string source)
    {
        Directory.CreateDirectory(Path.Combine(source, ".github", "workflows"));
        File.WriteAllText(Path.Combine(source, ".github", "workflows", "ci.yml"), "on: pull_request\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: npm test\n");
    }

    [Fact]
    public async Task The_fresh_verifier_does_not_run_and_the_body_names_the_required_checks_and_the_pre_push_filter()
    {
        using var f = new HandoverTests.Fixture(source: Ci);
        var verifier = new HandoverTests.FakeVerifier();
        var prs = new RulesPullRequests(ChecksGate.Required);

        var outcome = await new Handover(verifier, prs).RunAsync(f.Input(with: i => i with { CiVerifies = true, PrePushCheck = ["sh", "scripts/check.sh"] }), default);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        Assert.Empty(verifier.Calls);
        Assert.Null(outcome.Verification);
        var body = prs.Inner.Created.Single().Body;
        Assert.Contains("Verification: the repository's CI. The base branch main requires status checks; they decide the merge.", body);
        Assert.Contains("Before the push, the session driver ran `sh scripts/check.sh` in the session's container and it passed. That is a filter, not proof.", body);
    }

    [Fact]
    public async Task A_repository_with_ci_but_no_required_check_is_pushed_and_a_person_merges()
    {
        using var f = new HandoverTests.Fixture(untested: true, source: Ci);
        var prs = new RulesPullRequests(ChecksGate.None);

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), prs).RunAsync(f.Input(with: i => i with { CiVerifies = true }), default);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        var body = prs.Inner.Created.Single().Body;
        Assert.Contains("Verification: the repository's CI. No required check; a person merges.", body);
        Assert.Contains("Before the push, no check ran: the repository declares none (context.verify, driven.services.verify or scripts/check.sh).", body);
    }

    [Fact]
    public async Task A_repository_with_no_ci_and_no_check_is_refused()
    {
        using var f = new HandoverTests.Fixture(untested: true);
        var prs = new RulesPullRequests(ChecksGate.None);

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), prs).RunAsync(f.Input(with: i => i with { CiVerifies = true }), default);

        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Equal(ErrorCode.VerificationFailed, outcome.Error);
        Assert.Contains("no CI and no check", outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(prs.Inner.Created);
    }

    [Fact]
    public async Task A_documentation_change_of_a_repository_with_nothing_to_check_is_pushed()
    {
        using var f = new HandoverTests.Fixture(untested: true, file: "docs/notes.md");
        var prs = new RulesPullRequests(ChecksGate.None);

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), prs).RunAsync(f.Input(with: i => i with { CiVerifies = true }), default);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
    }
}
