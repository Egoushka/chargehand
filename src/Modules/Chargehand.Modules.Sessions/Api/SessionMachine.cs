using Chargehand.Modules.Runners.Api;

namespace Chargehand.Modules.Sessions.Api;

/// <summary>Where a session container stands for the server that waits on it (ADR 0060, rule 2). <c>Starting</c>: a start step runs again and looks at what an earlier attempt left;
/// <c>Checking</c>: it found an exited container and asks whether the output volume holds an outcome; <c>Waiting</c>: the container is the session and is waited on; <c>Interrupting</c>: a cancel
/// sent SIGINT and the grace period runs; <c>Ended</c>: the wait is over.</summary>
public enum SessionPhase { Starting, Checking, Waiting, Interrupting, Ended }

/// <summary>What the shell does next. <c>Create</c>: start the session; <c>Adopt</c>: wait on the container an earlier attempt left; <c>Remove</c>: remove the container, then create;
/// <c>ResetPrepAndRemove</c>: reset the workspace step and remove container and work volume, then create; <c>CheckOutcome</c>: look for the outcome on the output volume; <c>Inspect</c>: look at the
/// container; <c>Wait</c>: poll again after the interval; <c>End</c>: the session exited; <c>Kill</c>: SIGKILL; <c>Interrupt</c>: SIGINT and the grace period; <c>Leave</c>: leave the container
/// running for the next process.</summary>
public enum SessionAction { Create, Adopt, Remove, ResetPrepAndRemove, CheckOutcome, Inspect, Wait, End, Kill, Interrupt, Leave }

/// <param name="Deadline">While waiting: past it the session is killed (its wall clock plus a margin).</param>
/// <param name="Grace">How long an interrupted session gets before SIGKILL.</param>
public sealed record SessionState(SessionPhase Phase, DateTimeOffset Deadline = default, TimeSpan Grace = default)
{
    /// <summary>A start step that runs again, before it has looked at the container.</summary>
    public static SessionState Restarting { get; } = new(SessionPhase.Starting);

    public static SessionState Waiting(DateTimeOffset deadline, TimeSpan grace) => new(SessionPhase.Waiting, deadline, grace);
}

/// <summary>What the shell observed. Time comes in as an input: the machine reads no clock.</summary>
public abstract record SessionInput;

/// <summary>The container's status as the engine reports it; <paramref name="Now"/> matters only while waiting.</summary>
public sealed record SessionObserved(ContainerStatus Status, DateTimeOffset Now = default) : SessionInput;

public sealed record SessionOutcomeChecked(bool Present) : SessionInput;

/// <param name="Detaching">The server is stopping rather than the task being cancelled.</param>
public sealed record SessionCancelled(bool Detaching) : SessionInput;

/// <summary>Time since the interrupt was sent.</summary>
public sealed record SessionGraceTick(TimeSpan Elapsed) : SessionInput;

/// <summary>The server side of a session container (C4 brief, section 3): adopting what a cut start left, and waiting until it exits, its wall clock runs out or a cancel stops it. Pure: no I/O,
/// no clock; the task runner gathers the facts and carries out the command.</summary>
public static class SessionMachine
{
    public static (SessionState State, SessionAction Command) Decide(SessionState state, SessionInput input) => state.Phase switch
    {
        SessionPhase.Starting or SessionPhase.Checking => Adopting(state, input),
        SessionPhase.Waiting => Waiting(state, input),
        SessionPhase.Interrupting => Interrupting(state, input),
        _ => throw Unexpected(state, input),
    };

    /// <summary>The outcome a session left, or the one an exit without it stands for.</summary>
    public static SessionOutcome OutcomeOf(SessionOutcome? read, ContainerState exited) =>
        read ?? new SessionOutcome(SessionStatus.Failed, exited.OomKilled ? "out_of_memory" : "no_outcome", 0, 0, 0, 0, null, exited.ExitCode, [], false);

    /// <summary>A start that runs again adopts a running container or an exited one with its outcome; it removes one that was made and never started, and resets and removes one that ran
    /// without an outcome (it may have left commits in the work volume).</summary>
    private static (SessionState, SessionAction) Adopting(SessionState state, SessionInput input) => (state.Phase, input) switch
    {
        (SessionPhase.Starting, SessionObserved { Status: ContainerStatus.Missing }) => (state, SessionAction.Create),
        (SessionPhase.Starting, SessionObserved { Status: ContainerStatus.Running }) => (state with { Phase = SessionPhase.Waiting }, SessionAction.Adopt),
        (SessionPhase.Starting, SessionObserved { Status: ContainerStatus.Exited }) => (state with { Phase = SessionPhase.Checking }, SessionAction.CheckOutcome),
        (SessionPhase.Starting, SessionObserved) => (state, SessionAction.Remove),
        (SessionPhase.Checking, SessionOutcomeChecked { Present: true }) => (state with { Phase = SessionPhase.Waiting }, SessionAction.Adopt),
        (SessionPhase.Checking, SessionOutcomeChecked) => (state with { Phase = SessionPhase.Starting }, SessionAction.ResetPrepAndRemove),
        _ => throw Unexpected(state, input),
    };

    private static (SessionState, SessionAction) Waiting(SessionState state, SessionInput input) => input switch
    {
        SessionObserved { Status: not ContainerStatus.Running } => (state with { Phase = SessionPhase.Ended }, SessionAction.End),
        SessionObserved seen when seen.Now > state.Deadline => (state with { Phase = SessionPhase.Ended }, SessionAction.Kill),
        SessionObserved => (state, SessionAction.Wait),
        SessionCancelled { Detaching: true } => (state, SessionAction.Leave),
        SessionCancelled => (state with { Phase = SessionPhase.Interrupting }, SessionAction.Interrupt),
        _ => throw Unexpected(state, input),
    };

    /// <summary>The grace period is checked before the container is looked at; SIGKILL follows either way.</summary>
    private static (SessionState, SessionAction) Interrupting(SessionState state, SessionInput input) => input switch
    {
        SessionGraceTick tick when tick.Elapsed >= state.Grace => (state with { Phase = SessionPhase.Ended }, SessionAction.Kill),
        SessionGraceTick => (state, SessionAction.Inspect),
        SessionObserved { Status: ContainerStatus.Running } => (state, SessionAction.Wait),
        SessionObserved => (state with { Phase = SessionPhase.Ended }, SessionAction.Kill),
        _ => throw Unexpected(state, input),
    };

    private static InvalidOperationException Unexpected(SessionState state, SessionInput input) => new($"a session {state.Phase} cannot take {input.GetType().Name}");
}
