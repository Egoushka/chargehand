
namespace Chargehand.Modules.Oversight.Api;

/// <summary>ADR 0045's Budget decision: a task's budget comes from what tasks of its size class have cost. In this order: the task's own <c>budget_usd</c>; the <c>percentile</c>th percentile of the
/// spend of the size class's finished tasks in the last <c>window_days</c> days for the repository, when there are at least <c>min_tasks</c> of them; the same over all repositories, when the repository
/// alone has too few; else the flat budget of the size class. Pure: the learning records and the time are passed in, so each branch is tested with synthetic records.
/// <para>A finished task is one that completed, or one the governor stopped at its budget (<see cref="Governor.BudgetSpent"/>): the second is cut off at what it spent, and leaving it out would let a
/// budget that stops its tasks shrink each time. A task that failed, asked a question or was stopped for another reason says little about what the work costs. Only spends priced in full count:
/// a partial estimate (a model with no price) is a floor, and a task with no estimate has no figure.</para></summary>
public static class BudgetHistory
{
    public static BudgetChoice Choose(DrivenGovernor settings, string? size, decimal? taskBudget, string repository, IReadOnlyList<LearningRecord> records, DateTimeOffset now)
    {
        var flat = settings.Budget(size, taskBudget);
        if (taskBudget is not null)
            return flat;
        var history = settings.History ?? new DrivenGovernorHistory();
        var sizeClass = size ?? "M";
        var since = now.AddDays(-history.WindowDays);
        var finished = records.Where(r => r.SizeClass == sizeClass && r.At >= since && Finished(r) && r.Spend is { UsdEstimate: not null, UsdEstimatePartial: false }).ToList();
        var here = finished.Where(r => r.Repository == repository).ToList();
        var (scope, used) = here.Count >= history.MinTasks ? ("repository", here) : ("all", finished);
        if (used.Count < history.MinTasks || LearningStats.Percentile(used.Select(r => r.Spend.UsdEstimate!.Value), history.Percentile) is not { } budget || budget <= 0)
            return flat;
        return new BudgetChoice(Math.Round(budget, 4), new BudgetSource("history", used.Count, history.Percentile, scope));
    }

    private static bool Finished(LearningRecord record) => record.Completed || record.StopReason == Governor.BudgetSpent;
}
