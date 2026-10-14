using Chargehand.Contracts;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;
using static Chargehand.Tests.DrivenFakes;

namespace Chargehand.Modules.Tasks.Tests;

/// <summary>ADR 0039: one task of a batch as its <see cref="TaskRunner"/> runs it, on <see cref="TaskRunnerRig"/>: what a session's end makes of the task and a cancel while its container starts. Moved from <c>DrivenRunTests</c>, where they went through a whole batch.</summary>
public class TaskRunnerTests
{
    [Fact]
    public async Task A_session_image_newer_than_the_server_does_not_lose_its_outcome_or_report()
    {
        using var rig = new TaskRunnerRig(new FakeVolumes(_ => SessionStatus.Completed) { NewerSession = true });

        var outcome = await rig.RunAsync("Add a retry");

        Assert.Equal(TaskState.Completed, outcome.State);      // the outcome and the report parsed, so the handover ran
        Assert.Single(rig.Handover.Inputs);
    }

    [Fact]
    public async Task A_session_that_ended_on_a_usage_limit_is_a_rate_limited_task_not_a_session_failure()
    {
        using var rig = new TaskRunnerRig(new FakeVolumes(_ => SessionStatus.RateLimited));

        var outcome = await rig.RunAsync("Add a backoff");

        Assert.Equal((TaskState.Failed, ErrorCode.RateLimited), (outcome.State, outcome.Error));
        Assert.Empty(rig.Handover.Inputs);
    }

    [Fact]
    public async Task A_cancel_while_a_container_is_starting_still_removes_it_by_name()
    {
        using var rig = new TaskRunnerRig();
        rig.Engine.CancelOnStart = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.RunAsync("Add a retry"));

        var spec = rig.Engine.Started.Single();
        Assert.Contains($"rm chargehand-{spec.RunId}", rig.Engine.Calls);
    }

    [Fact]
    public void A_task_id_cannot_add_a_resource_attribute()
    {
        var env = new DrivenOtlpRoute("4318=collector-host:4318", "http://chargehand-driven:4318").Environment("run-1", "t1,chargehand.run_id=x", "run-2", null);
        Assert.Equal("chargehand.run_id=run-1,chargehand.task_id=t1%2Cchargehand.run_id%3Dx,chargehand.task_run_id=run-2", env["OTEL_RESOURCE_ATTRIBUTES"]);
    }
}
