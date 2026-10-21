using Chargehand.Contracts;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;

namespace Chargehand.Modules.Handover.Tests;

using Handover = Chargehand.Modules.Handover.Api.Handover;

/// <summary>CHARGEHAND-245: a repository with no test command is pushed when the change is documentation only, or when the base branch requires status checks, so the forge's CI is the gate.
/// Otherwise it is refused, as before.</summary>
public class HandoverNoTestsTests
{
    /// <summary>A forge whose base branch rules say <paramref name="gate"/>.</summary>
    private sealed class RulesPullRequests(ChecksGate gate) : IPullRequests, IBranchRules
    {
        public HandoverTests.FakePullRequests Inner { get; } = new();

        public List<string> Asked { get; } = [];

        public Task<ChecksGate> ChecksGateAsync(RemoteRepository repository, string branch, CancellationToken ct)
        {
            Asked.Add(branch);
            return Task.FromResult(gate);
        }

        public Task<PullRequestRef> CreateDraftAsync(RemoteRepository repo, string head, string @base, string title, string body, CancellationToken ct) => Inner.CreateDraftAsync(repo, head, @base, title, body, ct);

        public Task EditAsync(RemoteRepository repo, int number, string title, string body, CancellationToken ct) => Inner.EditAsync(repo, number, title, body, ct);

        public Task<PullRequestRef?> FindByHeadAsync(RemoteRepository repo, string head, CancellationToken ct) => Inner.FindByHeadAsync(repo, head, ct);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("docs/guide/intake.txt")]
    public async Task A_documentation_only_change_is_pushed_without_reading_the_rules(string file)
    {
        using var f = new HandoverTests.Fixture(untested: true, file: file);
        var prs = new RulesPullRequests(ChecksGate.None);

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), prs).RunAsync(f.Input(), default);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        Assert.Null(outcome.Verification);
        Assert.Empty(prs.Asked);
        Assert.Contains("It was pushed because every changed file is documentation", prs.Inner.Created.Single().Body);
    }

    [Fact]
    public async Task A_code_change_is_pushed_when_the_base_branch_requires_status_checks()
    {
        using var f = new HandoverTests.Fixture(untested: true);
        var prs = new RulesPullRequests(ChecksGate.Required);

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), prs).RunAsync(f.Input(), default);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        Assert.Equal(["main"], prs.Asked);
        Assert.Contains("the base branch main requires status checks", prs.Inner.Created.Single().Body);
    }

    [Theory]
    [InlineData(ChecksGate.None)]
    [InlineData(ChecksGate.Unknown)]
    public async Task A_code_change_is_refused_when_nothing_gates_it(ChecksGate gate)
    {
        using var f = new HandoverTests.Fixture(untested: true);
        var prs = new RulesPullRequests(gate);

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), prs).RunAsync(f.Input(), default);

        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Equal(ErrorCode.VerificationFailed, outcome.Error);
        Assert.Contains("no test command", outcome.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(prs.Inner.Created);
        Assert.Equal("", f.RemoteRef("chargehand/run1"));
    }

    [Fact]
    public async Task A_forge_that_cannot_read_rules_refuses_a_code_change_as_before()
    {
        using var f = new HandoverTests.Fixture(untested: true);

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), new HandoverTests.FakePullRequests()).RunAsync(f.Input(), default);

        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
    }

    [Theory]
    [InlineData("README.md", true)]
    [InlineData("docs/adr/0046.md", true)]
    [InlineData("docs/diagram.svg", true)]
    [InlineData("src/docs/x.cs", false)]
    [InlineData("readme.txt", false)]
    public void Documentation_is_markdown_anywhere_or_anything_under_docs(string path, bool expected) =>
        Assert.Equal(expected, Handover.IsDocumentation(path));
}
