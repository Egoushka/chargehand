using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Tests;

namespace Chargehand.Modules.Handover.Tests;

/// <summary>CHARGEHAND-244: auto-merge waits only for required checks, so on a merge repository whose base branch requires none the integrator asks instead: ready, one comment, no merge.</summary>
public class IntegratorChecksGateTests
{
    private static readonly RemoteRepository Repo = new("o", "r");

    private static Integrator Make(FakeIntegrationHost host, string mode = "merge") =>
        new(new IntegratorPolicy(new DrivenIntegrator(1, 2, "k", new Dictionary<string, string> { ["o/r"] = mode })), host, delay: (_, _) => Task.CompletedTask);

    private static IntegrationTarget Target() => new(Repo, 7, "chargehand/run1", false, "Add a retry");

    [Theory]
    [InlineData(ChecksGate.None, "has no rule that requires a status check")]
    [InlineData(ChecksGate.Unknown, "could not read the rules")]
    public async Task A_merge_repository_without_required_checks_is_marked_ready_and_asked_about(ChecksGate gate, string why)
    {
        var host = new FakeIntegrationHost { Gate = gate, BehindBy = 2, StatusAfterReady = "CLEAN" };
        List<string> told = [];

        var result = await Make(host).IntegrateAsync(Target(), told.Add, default);

        Assert.Equal(IntegrationAction.Asked, result.Action);
        Assert.Contains(why, result.Detail);
        Assert.Contains("treated as ask", result.Detail);
        Assert.Equal(["get", "ready", "get", "comment"], host.Calls);
        Assert.Contains("no-required-checks", Assert.Single(host.Comments));
    }

    [Fact]
    public async Task The_reason_is_commented_once_across_repeated_passes()
    {
        var host = new FakeIntegrationHost { Gate = ChecksGate.None };

        await Make(host).IntegrateAsync(Target(), _ => { }, default);
        await Make(host).IntegrateAsync(Target(), _ => { }, default);

        Assert.Single(host.Comments);
    }

    [Fact]
    public async Task A_merge_repository_with_required_checks_still_enables_auto_merge()
    {
        var host = new FakeIntegrationHost { Gate = ChecksGate.Required };

        var result = await Make(host).IntegrateAsync(Target(), _ => { }, default);

        Assert.Equal(IntegrationAction.AutoMergeEnabled, result.Action);
        Assert.Contains("auto-merge aaaaaaa1", host.Calls);
    }

    [Fact]
    public async Task An_ask_repository_does_not_read_the_rules_and_says_what_it_always_said()
    {
        var host = new FakeIntegrationHost { Gate = ChecksGate.None };

        var result = await Make(host, "ask").IntegrateAsync(Target(), _ => { }, default);

        Assert.Equal(IntegrationAction.Asked, result.Action);
        Assert.Contains("policy is ask", result.Detail);
    }

    [Fact]
    public async Task Following_a_merge_repository_without_required_checks_stops_at_once()
    {
        var host = new FakeIntegrationHost { Gate = ChecksGate.None, Current = FakeIntegrationHost.Snapshot(draft: false, status: "CLEAN") };

        var result = await Make(host).FollowAsync(Target(), (_, _) => throw new InvalidOperationException("no fix expected"), _ => { }, default);

        Assert.Equal(IntegrationAction.None, result.Action);
        Assert.Contains("requires no checks", result.Detail);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("merge", StringComparison.Ordinal) || c.StartsWith("auto-merge", StringComparison.Ordinal));
    }
}
