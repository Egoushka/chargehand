namespace Chargehand.Automation;

/// <param name="Trigger"><c>schedule</c> or <c>manual</c>.</param>
public sealed record SweepReport(string Id, string Trigger, DateTimeOffset Started, DateTimeOffset Finished, IReadOnlyList<SweepRepoRow> Repositories);

/// <param name="Problem">One line on why the repository was not swept; null when it was.</param>
public sealed record SweepRepoRow(string Repository, int Ready, int NeedsYou, int Blocked, int Epic, int Skipped, int Enqueued, string? Problem, int Deferred = 0);
