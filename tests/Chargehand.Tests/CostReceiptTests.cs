using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>The cost receipt (CHARGEHAND-288): the footer of a task's pull request body says what the task cost in the governor's dollar equivalent, how many turns it took and how many continuation
/// sessions it needed, so a reviewer sees what the change cost before reading it.</summary>
public class CostReceiptTests
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";

    private static SessionOutcome Session(decimal? usd = 1.2345m, int turns = 54, bool partial = false, SessionStatus status = SessionStatus.Completed) =>
        new(status, "", turns, 1000, 200, 4000, null, 0, [], true, Commits: 3, UsdEstimate: usd, UsdEstimatePartial: partial);

    private static RunRequest One() =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([new DrivenTask("t1", Goal: "one")], MaxParallel: 1, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    [Theory]
    [InlineData(1.2345, 54, false, 0, "chargehand cost receipt: $1.23 notional, 54 turns, no continuations.")]
    [InlineData(0.4, 1, false, 1, "chargehand cost receipt: $0.40 notional, 1 turn, 1 continuation.")]
    [InlineData(12.0, 80, false, 3, "chargehand cost receipt: $12.00 notional, 80 turns, 3 continuations.")]
    [InlineData(2.5, 30, true, 0, "chargehand cost receipt: at least $2.50 notional (a model had no price), 30 turns, no continuations.")]
    [InlineData(null, 30, false, 0, "chargehand cost receipt: not priced, 30 turns, no continuations.")]
    public void The_receipt_says_the_cost_the_turns_and_the_continuations(double? usd, int turns, bool partial, int continuations, string expected)
    {
        var footer = CostReceipt.Footer(Session(usd is null ? null : (decimal)usd, turns, partial), continuations);

        Assert.Equal("\n\n---\n" + expected, footer);
    }

    [Fact]
    public async Task A_finished_tasks_pull_request_body_ends_with_the_receipt()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: (_, _) => Session());

        await rig.Run.RunAsync(One(), "run-batch-r", g => $"tok-{g.RunId}", default);

        var body = Assert.Single(rig.Handover.Inputs).Body;
        Assert.EndsWith("\n\n---\nchargehand cost receipt: $1.23 notional, 54 turns, no continuations.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_partial_drafts_body_ends_with_the_receipt_of_all_its_sessions()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: (_, _) => Session(0.5m, 80, status: SessionStatus.Stalled));

        await rig.Run.RunAsync(One(), "run-batch-p", g => $"tok-{g.RunId}", default);

        var input = Assert.Single(rig.Handover.Inputs);
        Assert.True(input.Partial);
        Assert.Matches(@"\n\n---\nchargehand cost receipt: \$\d+\.\d\d notional, \d+ turns?, [^\n]+\.$", input.Body);
    }
}
