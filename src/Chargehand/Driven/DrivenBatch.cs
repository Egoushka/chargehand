using Chargehand.Config;
using Chargehand.Contracts;

namespace Chargehand.Driven;

/// <summary>A request's <c>driven</c> block, made ready to run (ADR 0039): tasks resolved to goals, limits derived from the request, the profile and the
/// preset.</summary>
public static class DrivenBatch
{
    /// <summary>The most tasks of one batch that run at once, whatever the profile says, until it is measured on the hosts that run them.</summary>
    public const int ParallelCeiling = 4;

    /// <returns>The tasks ready to run, and the ones that failed to resolve (their outcome, already final).</returns>
    /// <exception cref="ChargehandException">The profile has driven sessions off; ids repeat; or a task names a <c>ref</c> and no task source is configured.</exception>
    public static async Task<(IReadOnlyList<ResolvedTask> Ready, IReadOnlyList<TaskOutcome> Failed)> PrepareAsync(RunRequest request, DrivenSettings profile, ITaskSource? source,
        CancellationToken ct)
    {
        if (!profile.Enabled)
            throw new ChargehandException(ErrorCode.InvalidRequest, "driven sessions are off in this profile", "Set driven.enabled to true in the profile, and configure driven.images and the runner or Docker.");
        var driven = request.Driven ?? throw new ChargehandException(ErrorCode.InvalidRequest, "the request has no driven block");
        if (DrivenRules.Problems(request) is { Count: > 0 } problems)
            throw new ChargehandException(ErrorCode.InvalidRequest, string.Join("; ", problems), "Fix the driven block; ids are unique and context.repository is required.");
        if (source is null && driven.Tasks.Any(t => t.Goal is null && t.Ref is not null))
            throw new ChargehandException(ErrorCode.InvalidRequest, "a task names a ref but no task source is configured",
                "Give the task a goal, or configure driven.task_source in the profile (docs/guide/driven.md).");

        List<ResolvedTask> ready = [];
        List<TaskOutcome> failed = [];
        foreach (var task in driven.Tasks)
        {
            if (task.Goal is { Length: > 0 } goal)
            {
                ready.Add(new ResolvedTask(task.Id, task.Ref, goal));
                continue;
            }
            try
            {
                ready.Add(new ResolvedTask(task.Id, task.Ref, await source!.ResolveAsync(task.Ref!, ct)));
            }
            catch (TaskSourceException e)
            {
                failed.Add(new TaskOutcome(task.Id, TaskState.Failed, null, $"could not resolve '{task.Ref}': {e.Message}", ErrorCode.InvalidRequest));
            }
        }
        return (ready, failed);
    }

    /// <param name="priced">The credential has a dollar price (an API key): the dollar cap is required and enforced. A subscription has none, so the token cap is required.</param>
    /// <param name="perTaskUsd">A task's dollar cap where the credential is priced.</param>
    public static BatchLimits Limits(RunRequest request, DrivenSettings profile, DrivenPreset preset, decimal perTaskUsd, bool priced, SemaphoreSlim? global)
    {
        var driven = request.Driven!;
        if (priced && driven.MaxUsdTotal is null)
            throw new ChargehandException(ErrorCode.InvalidRequest, "driven.max_usd_total is required with an API key: the batch needs a dollar cap", "Add max_usd_total to the driven block.");
        if (!priced && driven.MaxTokensTotal is null)
            throw new ChargehandException(ErrorCode.InvalidRequest, "driven.max_tokens_total is required with a subscription: it has no dollar price to cap", "Add max_tokens_total to the driven block.");
        var parallel = Math.Clamp(Math.Min(driven.MaxParallel ?? profile.MaxParallel, profile.MaxParallel), 1, ParallelCeiling);
        return new BatchLimits(parallel, driven.MaxTokensTotal, priced ? driven.MaxUsdTotal : null,
            new TaskLimits(preset.MaxTokens ?? 2_000_000, priced ? perTaskUsd : null), global);
    }
}
