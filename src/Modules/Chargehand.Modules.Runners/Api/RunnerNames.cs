namespace Chargehand.Modules.Runners.Api;

/// <summary>The volume names a run's containers use (the runner insists on them).</summary>
public static class RunnerNames
{
    public static string Work(string runId) => $"chargehand-work-{runId}";

    public static string Out(string runId) => $"chargehand-out-{runId}";
}
