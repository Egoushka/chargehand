namespace Chargehand.Automation;

/// <param name="Trigger"><c>schedule</c> or <c>manual</c>.</param>
public sealed record SweepReport(string Id, string Trigger, DateTimeOffset Started, DateTimeOffset Finished, IReadOnlyList<SweepRepoRow> Repositories, IReadOnlyList<SweepNeedsYou>? NeedsYouItems = null);

/// <summary>An issue the sorter left to the owner; <paramref name="Reason"/> is the sorter's own.</summary>
public sealed record SweepNeedsYou(string Repository, int Issue, string Title, string Reason, string Url);

/// <param name="Running">Not stored: the server fills it at read time from the task pool's claimed items.</param>
/// <param name="Problem">One line on why the repository was not swept; null when it was.</param>
public sealed record SweepRepoRow(string Repository, int Ready, int NeedsYou, int Blocked, int Epic, int Skipped, int Enqueued, string? Problem, int Deferred = 0, int Running = 0);
