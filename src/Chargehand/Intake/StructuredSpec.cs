using Chargehand.Config;
using Chargehand.Contracts;

namespace Chargehand.Intake;

/// <summary>
/// The Task Spec of a request a structured caller sent complete (issue 429): automation's pull request reviews send a fixed preset, a
/// pinned commit and the goal as an input, and wait for no one. Intake's model call cannot change what such a run does, so with the
/// profile's <c>skip_structured_intake</c> on the spec is built here instead, and the run starts a median 9 s earlier.
/// </summary>
public static class StructuredSpec
{
    public const string GoalKind = "goal";

    /// <summary>The spec for a structured request, or null when intake must run: an interactive caller (intake may ask), a preset that allows
    /// another action than answer (intake chooses it) or has approval thresholds (they read intake's risk and estimate), or no non-empty goal input.</summary>
    public static TaskSpec? For(RunRequest request, Preset preset, NodeKind kind, string runId)
    {
        if (request.Context.Interactive || preset.AllowedActions.Any(a => a != "answer"))
            return null;
        if (preset.Approval is { AskAboveRisk: not null } or { AskAboveUsd: not null })
            return null;
        if (request.Inputs?.FirstOrDefault(i => i.Kind == GoalKind) is not { Text: { } goal } || string.IsNullOrWhiteSpace(goal))
            return null;
        return new TaskSpec("task-spec/v1", runId, goal, [], [], Risk.Low,
            new Estimate(0, kind.Budget.MaxInputTokens, 0, kind.Budget.MaxUsd, "the preset's node budget; intake skipped for a structured request"), TaskAction.Answer, null);
    }
}
