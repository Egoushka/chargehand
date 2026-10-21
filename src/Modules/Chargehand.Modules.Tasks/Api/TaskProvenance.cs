using Chargehand.Kernel;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Modules.Tasks.Api;

/// <summary>The trailers of a driven session's commits (ADR 0070), worked out from the task and the session's outcome file.</summary>
internal static class TaskProvenance
{
    /// <param name="spent">The session whose commits these are; for a partial handover, the task's total, since the branch holds the work of several sessions.</param>
    public static Provenance Of(ResolvedTask task, string runId, SessionOutcome spent, string model, string claudeVersion)
    {
        var input = spent.InputTokens + spent.CacheReadTokens + spent.CacheWriteTokens;
        return new Provenance(runId, task.Ref is { Length: > 0 } tracker ? $"{task.Id} ({tracker})" : task.Id, $"claude-code {claudeVersion}".Trim(), spent.Model ?? model,
            spent.CostUsd ?? spent.UsdEstimate, spent.InputTokens + spent.OutputTokens, input == 0 ? null : (int)(spent.CacheReadTokens * 100 / input), ChecksOf(spent.Checks));
    }

    private static string? ChecksOf(IReadOnlyList<CheckResult>? checks) =>
        checks is not { Count: > 0 } ? null : $"{checks.Count(c => c.Passed)} of {checks.Count} checks passed";
}
