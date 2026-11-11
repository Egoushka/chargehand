namespace Chargehand.Modules.Oversight.Api;

/// <summary>One repository's tasks over one window (issue #802).</summary>
/// <param name="Repository">As the learning record names it; <c>*</c> in a total.</param>
/// <param name="BudgetRefusedTasks">Tasks whose last session the model gateway ended over a spend budget. The record keeps that a budget refusal ended the task, not its HTTP status: a 402, or a
/// 429 whose text names a budget, both count.</param>
/// <param name="MergedPrs">Tasks whose pull request was merged, by the latest outcome the server read back.</param>
public sealed record BudgetRefusalRow(string Repository, int Tasks, int BudgetRefusedTasks, int MergedPrs);

/// <param name="Total">All repositories of the window.</param>
public sealed record BudgetRefusalWindow(int Days, DateTimeOffset Since, BudgetRefusalRow Total, IReadOnlyList<BudgetRefusalRow> Repositories);

/// <summary>How many driven tasks a gateway's budget refusal ended, and how many pull requests merged, per repository over 7 and 30 days: the figures a weekly check reads, from the learning
/// records next to the run log. A task is placed in a window by when it ended, and a pull request by its outcome now. Pure: the same entries give the same windows.</summary>
public static class BudgetRefusals
{
    /// <summary>The <c>stop_reason</c> of a task whose last session the gateway refused over a spend budget; the sessions module's <c>GatewayRefusal.BudgetReason</c>.</summary>
    public const string StopReason = "budget_exhausted";

    public static readonly IReadOnlyList<int> DefaultWindows = [7, 30];

    public static IReadOnlyList<BudgetRefusalWindow> Build(IReadOnlyList<LearningEntry> entries, DateTimeOffset now, IReadOnlyList<int>? windows = null) =>
        [.. (windows ?? DefaultWindows).Order().Select(days => Window(days, now, [.. entries.Where(e => e.Record.At >= now.AddDays(-days))]))];

    private static BudgetRefusalWindow Window(int days, DateTimeOffset now, List<LearningEntry> entries) =>
        new(days, now.AddDays(-days), Row("*", entries),
            [.. entries.GroupBy(e => e.Record.Repository).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => Row(g.Key, [.. g]))]);

    private static BudgetRefusalRow Row(string repository, List<LearningEntry> entries) =>
        new(repository, entries.Count, entries.Count(e => e.Record.StopReason == StopReason), entries.Count(Merged));

    private static bool Merged(LearningEntry e) =>
        e.Record.Pr is not null && e.PrOutcome?.Outcome is PrOutcomes.Merged or PrOutcomes.MergedUnchanged or PrOutcomes.MergedEdited;
}
