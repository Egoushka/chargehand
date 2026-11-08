using Chargehand.Modules.Journal.Api;

namespace Chargehand.Modules.Handover.Tests;

using Handover = Chargehand.Modules.Handover.Api.Handover;

/// <summary>ADR 0090, first slice: each handover step that ends is appended to the run's action log beside its bundle.</summary>
public class HandoverActionLogTests
{
    [Fact]
    public async Task Each_handover_step_is_an_event_in_the_run_s_action_log_in_order()
    {
        using var f = new HandoverTests.Fixture();
        var input = f.Input();

        await new Handover(new HandoverTests.FakeVerifier(), new HandoverTests.FakePullRequests()).RunAsync(input, default);

        var steps = ActionLog.Replay(File.ReadLines(Path.Combine(input.OutDirectory, ActionLog.FileName))).ToList();
        Assert.Equal(3, steps.Count);
        Assert.Contains(" chargehand.handover.verify_finished verify_finished: tests passed", steps[0], StringComparison.Ordinal);
        Assert.Contains(" chargehand.handover.pushed pushed: pushed chargehand/run1", steps[1], StringComparison.Ordinal);
        Assert.Contains(" chargehand.handover.pr_opened pr_opened: draft pull request opened", steps[2], StringComparison.Ordinal);
    }
}
