namespace Chargehand.Modules.Warmer.Api;

/// <summary>Runs one step and returns its exit code. Tests give a fake; the host runs it in the sandbox sessions use.</summary>
public interface IStepRunner
{
    Task<int> RunAsync(WarmStep step, CancellationToken ct);
}
