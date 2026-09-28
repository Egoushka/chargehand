namespace Chargehand.Config;

/// <summary>Where prompts/, presets/ and the run log live (ADR 0027, follow-up 2).</summary>
public static class InstallPaths
{
    /// <summary>
    /// Root: the working directory when it holds both prompts/ and presets/ (a checkout, the container's /app), else the
    /// app's own directory, where the build copies them. Run log: the profile's run_log; unset, runs/run-log.jsonl
    /// relative to a checkout as before, else chargehand/run-log.jsonl under the per-user data directory, never the
    /// caller's workspace.
    /// </summary>
    public static (string Root, string RunLog) Resolve(string? profileRunLog, string workingDirectory, string appDirectory, string userData)
    {
        var checkout = Directory.Exists(Path.Combine(workingDirectory, "prompts")) && Directory.Exists(Path.Combine(workingDirectory, "presets"));
        return (checkout ? workingDirectory : appDirectory,
            profileRunLog ?? (checkout ? "runs/run-log.jsonl" : Path.Combine(userData, "chargehand", "run-log.jsonl")));
    }
}
