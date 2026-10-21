using Chargehand.Contracts;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Tests;

namespace Chargehand.Modules.Handover.Tests;

// Inside this namespace, "Handover" is the module's namespace; the alias names the class.
using Handover = Chargehand.Modules.Handover.Api.Handover;

/// <summary>ADR 0069: the second line of defence. Whatever decided the repository is local-only, the hand-over, the integrator and the CI fixer each refuse on their own.</summary>
public class LocalOnlyTests
{
    private sealed class CountingPullRequests : IPullRequests
    {
        public int Calls { get; private set; }

        public Task<PullRequestRef> CreateDraftAsync(RemoteRepository repo, string head, string @base, string title, string body, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new PullRequestRef("https://example.test/o/r/pull/9", 9, false));
        }

        public Task EditAsync(RemoteRepository repo, int number, string title, string body, CancellationToken ct)
        {
            Calls++;
            return Task.CompletedTask;
        }

        public Task<PullRequestRef?> FindByHeadAsync(RemoteRepository repo, string head, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<PullRequestRef?>(null);
        }
    }

    [Fact]
    public async Task The_handover_of_a_local_only_repository_pushes_nothing_opens_no_pull_request_and_starts_no_verification()
    {
        using var f = new HandoverTests.Fixture();
        var prs = new CountingPullRequests();
        var verifier = new HandoverTests.FakeVerifier();

        var outcome = await new Handover(verifier, prs).RunAsync(f.Input(with: i => i with { LocalOnly = true }), default);

        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Equal(ErrorCode.LocalOnly, outcome.Error);
        Assert.Equal(0, prs.Calls);
        Assert.Empty(verifier.Calls);
        Assert.Equal("", f.RemoteRef("refs/heads/chargehand/run1"));
    }

    [Fact]
    public async Task The_integrator_of_a_local_only_repository_calls_no_forge()
    {
        var host = new FakeIntegrationHost();
        var integrator = new Integrator(new IntegratorPolicy(new DrivenIntegrator(1, 2, "k", new Dictionary<string, string> { ["o/r"] = "merge" })), host, delay: (_, _) => Task.CompletedTask);
        var target = new IntegrationTarget(new RemoteRepository("o", "r"), 7, "chargehand/run1", false, "Add a retry", LocalOnly: true);

        var result = await integrator.IntegrateAsync(target, _ => { }, default);

        Assert.Equal(IntegrationAction.None, result.Action);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task The_ci_fixer_of_a_local_only_repository_runs_no_session_and_calls_no_forge()
    {
        var host = new FakeIntegrationHost();
        var integrator = new Integrator(new IntegratorPolicy(new DrivenIntegrator(1, 2, "k", new Dictionary<string, string> { ["o/r"] = "merge" })), host, new DrivenCiFix(MaxAttempts: 3),
            delay: (_, _) => Task.CompletedTask);
        var target = new IntegrationTarget(new RemoteRepository("o", "r"), 7, "chargehand/run1", false, "Add a retry", LocalOnly: true);
        var sessions = 0;

        var result = await integrator.FollowAsync(target, (_, _) =>
        {
            sessions++;
            return Task.FromResult(new CiFixResult(true, "pushed"));
        }, _ => { }, default);

        Assert.Equal(IntegrationAction.None, result.Action);
        Assert.Equal(0, sessions);
        Assert.Empty(host.Calls);
    }
}
