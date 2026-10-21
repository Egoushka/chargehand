using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Api;

/// <summary>The profile's <c>driven</c> keys a batch is prepared with.</summary>
/// <param name="Enabled"><c>driven.enabled</c>.</param>
/// <param name="MaxParallel"><c>driven.max_parallel</c>: the most tasks of one batch at once.</param>
public sealed record BatchOptions(bool Enabled, int MaxParallel);

/// <summary>Turns a tracker item id into a goal; the host supplies it from its task source.</summary>
/// <exception cref="TaskSourceException">The item cannot be read or has no title.</exception>
public delegate Task<string> TaskSourceResolver(string @ref, CancellationToken ct);

public sealed class TaskSourceException(string message) : Exception(message);

/// <summary>A request's <c>driven</c> block, made ready to run (ADR 0039): tasks resolved to goals, limits derived from the request, the profile and the
/// preset.</summary>
public static class DrivenBatch
{
    /// <summary>The most tasks of one batch that run at once, whatever the profile says. Memory admission and the rate-limit pause govern how many really run (ADR 0045); this only stops a typo.</summary>
    public const int ParallelCeiling = 16;

    /// <returns>The tasks ready to run, and the ones that failed to resolve (their outcome, already final).</returns>
    /// <exception cref="ChargehandException">The profile has driven sessions off; ids repeat; a task names a <c>ref</c> and no task source is configured; or a task continues one the run log cannot continue from.</exception>
    public static async Task<(IReadOnlyList<ResolvedTask> Ready, IReadOnlyList<TaskOutcome> Failed)> PrepareAsync(RunRequest request, BatchOptions profile, TaskSourceResolver? source,
        CancellationToken ct, ContinuationSource? continuations = null)
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
            if (task.ContinueFrom is not null)
            {
                ready.Add(await (continuations ?? throw new ChargehandException(ErrorCode.InvalidRequest, "a task continues an earlier one, but there is no run log to read it from")).ResolveAsync(task, ct));
                continue;
            }
            if (task.Goal is { Length: > 0 } goal)
            {
                ready.Add(new ResolvedTask(task.Id, task.Ref, goal, task.MaxTurns, task.MaxMinutes, task.Size, task.BudgetUsd, DependsOn: task.DependsOn, Title: task.Title,
                    Acceptance: task.Acceptance));
                continue;
            }
            try
            {
                ready.Add(new ResolvedTask(task.Id, task.Ref, await source!(task.Ref!, ct), task.MaxTurns, task.MaxMinutes, task.Size, task.BudgetUsd, DependsOn: task.DependsOn, Title: task.Title,
                    Acceptance: task.Acceptance));
            }
            catch (TaskSourceException e)
            {
                failed.Add(new TaskOutcome(task.Id, TaskState.Failed, null, $"could not resolve '{task.Ref}': {e.Message}", ErrorCode.InvalidRequest));
            }
        }
        // A task cannot start from work that was never started: whatever depends on a task that did not resolve, directly or down the chain, is not started either.
        for (var again = true; again;)
        {
            again = false;
            foreach (var task in ready.Where(t => t.DependsOn is { Count: > 0 }).ToList())
                if (task.DependsOn!.FirstOrDefault(d => failed.Any(f => f.Id == d)) is { } cause)
                {
                    ready.Remove(task);
                    failed.Add(new TaskOutcome(task.Id, TaskState.NotStarted, null, $"not started: the task it depends on, '{cause}', could not be resolved", ErrorCode.InvalidRequest));
                    again = true;
                }
        }
        return (ready, failed);
    }

    /// <param name="priced">The credential has a dollar price (an API key): the dollar cap is required and enforced. A subscription has none, so the token cap is required.</param>
    /// <param name="perTaskUsd">A task's dollar cap where the credential is priced.</param>
    public static BatchLimits Limits(RunRequest request, BatchOptions profile, DrivenPreset preset, decimal perTaskUsd, bool priced, SemaphoreSlim? global)
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
