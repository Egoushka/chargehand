using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Modules.Sessions.Tests;

/// <summary>C4 slice S3: the session machine as a decision table, one row per transition. No engine, no clock, no file system.</summary>
public class SessionMachineTests
{
    private static readonly DateTimeOffset Deadline = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);
    private static readonly SessionState Waiting = SessionState.Waiting(Deadline, Grace);
    private static readonly SessionState Checking = SessionState.Restarting with { Phase = SessionPhase.Checking };
    private static readonly SessionState Interrupting = Waiting with { Phase = SessionPhase.Interrupting };

    public static TheoryData<string, SessionState, SessionInput, SessionPhase, SessionAction> Rows() => new()
    {
        // A start step that runs again (TaskRunner.AdoptAsync).
        { "restarted start, container missing: create it", SessionState.Restarting, new SessionObserved(ContainerStatus.Missing), SessionPhase.Starting, SessionAction.Create },
        { "restarted start, container running: adopt it", SessionState.Restarting, new SessionObserved(ContainerStatus.Running), SessionPhase.Waiting, SessionAction.Adopt },
        { "restarted start, container exited: look for its outcome", SessionState.Restarting, new SessionObserved(ContainerStatus.Exited), SessionPhase.Checking, SessionAction.CheckOutcome },
        { "restarted start, container created and never started: remove it", SessionState.Restarting, new SessionObserved(ContainerStatus.Created), SessionPhase.Starting, SessionAction.Remove },
        { "exited with its outcome: adopt it", Checking, new SessionOutcomeChecked(true), SessionPhase.Waiting, SessionAction.Adopt },
        { "exited without an outcome: reset the workspace step and remove it", Checking, new SessionOutcomeChecked(false), SessionPhase.Starting, SessionAction.ResetPrepAndRemove },
        // The wait (TaskRunner.WaitAsync).
        { "running within its wall clock: wait", Waiting, new SessionObserved(ContainerStatus.Running, Deadline), SessionPhase.Waiting, SessionAction.Wait },
        { "running past its wall clock and margin: kill", Waiting, new SessionObserved(ContainerStatus.Running, Deadline.AddTicks(1)), SessionPhase.Ended, SessionAction.Kill },
        { "exited: the wait ends", Waiting, new SessionObserved(ContainerStatus.Exited, Deadline), SessionPhase.Ended, SessionAction.End },
        { "missing: the wait ends", Waiting, new SessionObserved(ContainerStatus.Missing, Deadline), SessionPhase.Ended, SessionAction.End },
        { "created: the wait ends", Waiting, new SessionObserved(ContainerStatus.Created, Deadline), SessionPhase.Ended, SessionAction.End },
        { "exited past its wall clock: the wait ends, no kill", Waiting, new SessionObserved(ContainerStatus.Exited, Deadline.AddHours(1)), SessionPhase.Ended, SessionAction.End },
        { "cancelled: interrupt", Waiting, new SessionCancelled(false), SessionPhase.Interrupting, SessionAction.Interrupt },
        { "cancelled while the server stops: leave it running", Waiting, new SessionCancelled(true), SessionPhase.Waiting, SessionAction.Leave },
        // The interrupt's grace period (TaskRunner.InterruptAsync).
        { "interrupted, within the grace: look at it", Interrupting, new SessionGraceTick(Grace - TimeSpan.FromTicks(1)), SessionPhase.Interrupting, SessionAction.Inspect },
        { "interrupted, grace over: kill", Interrupting, new SessionGraceTick(Grace), SessionPhase.Ended, SessionAction.Kill },
        { "interrupted and still running: wait", Interrupting, new SessionObserved(ContainerStatus.Running), SessionPhase.Interrupting, SessionAction.Wait },
        { "interrupted and exited: kill all the same", Interrupting, new SessionObserved(ContainerStatus.Exited), SessionPhase.Ended, SessionAction.Kill },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void Decides(string row, SessionState state, SessionInput input, SessionPhase phase, SessionAction command)
    {
        var (next, did) = SessionMachine.Decide(state, input);

        Assert.True((phase, command) == (next.Phase, did), $"{row}: got {next.Phase}, {did}");
        Assert.Equal((state.Deadline, state.Grace), (next.Deadline, next.Grace));
    }

    public static TheoryData<string, SessionState, SessionInput> Refused() => new()
    {
        { "a restarted start cannot be cancelled", SessionState.Restarting, new SessionCancelled(false) },
        { "an outcome check before the container was seen", SessionState.Restarting, new SessionOutcomeChecked(true) },
        { "a wait has no outcome check", Waiting, new SessionOutcomeChecked(true) },
        { "an interrupt is not cancelled again", Interrupting, new SessionCancelled(false) },
        { "an ended session takes nothing", Waiting with { Phase = SessionPhase.Ended }, new SessionObserved(ContainerStatus.Running) },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Refuses_an_input_its_phase_does_not_take(string row, SessionState state, SessionInput input) =>
        Assert.True(Assert.Throws<InvalidOperationException>(() => SessionMachine.Decide(state, input)) is not null, row);

    [Fact]
    public void An_exit_without_an_outcome_stands_for_no_outcome()
    {
        var outcome = SessionMachine.OutcomeOf(null, new ContainerState(ContainerStatus.Exited, 137, false));

        Assert.Equal((SessionStatus.Failed, "no_outcome", 137, false), (outcome.Status, outcome.Reason, outcome.ExitCode, outcome.BundleWritten));
    }

    [Fact]
    public void An_exit_without_an_outcome_after_the_kernel_killed_it_stands_for_out_of_memory() =>
        Assert.Equal("out_of_memory", SessionMachine.OutcomeOf(null, new ContainerState(ContainerStatus.Exited, 137, true)).Reason);

    [Fact]
    public void An_outcome_the_session_left_stands()
    {
        var left = new SessionOutcome(SessionStatus.Completed, "", 9, 1, 2, 3, null, 0, [], true);

        Assert.Same(left, SessionMachine.OutcomeOf(left, new ContainerState(ContainerStatus.Exited, 0, true)));
    }
}
