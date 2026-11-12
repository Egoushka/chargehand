namespace Chargehand.Modules.Warmer.Api;

/// <summary>Runs one step and returns how it ended. Tests give a fake; the host runs it in the sandbox sessions use.</summary>
public interface IStepRunner
{
    Task<StepExit> RunAsync(WarmStep step, CancellationToken ct);
}

/// <summary>How a step ended: its exit code, whether its time ran out, and the end of its output, which can explain a failure.</summary>
public sealed record StepExit(int Code, bool TimedOut = false, string OutputTail = "");
