using System.Globalization;

namespace Chargehand.Driven;

/// <summary>One bucket of the learning records over time, for one group (or all tasks when not grouped). Tokens are the task's whole usage, the CI fix sessions' included.</summary>
/// <param name="Group">The repository, account (<c>owner</c> for a record with no actor) or size class the point belongs to; null when not grouped.</param>
/// <param name="PassRate">Completed over all tasks of the point; null for no tasks.</param>
/// <param name="UsdEstimate">The sum of the dollar equivalents that are known; <paramref name="UsdEstimateKnown"/> says for how many tasks.</param>
/// <param name="Checkpointed">Tasks with a checkpoint decision, acted on or only logged.</param>
/// <param name="CheckpointedCompleted">Of those, the ones that completed (ADR 0045's false-stop proxy).</param>
public sealed record SeriesPoint(DateTimeOffset BucketStart, string? Group, int Tasks, int Completed, int Partial, int Failed, int NeedsInput, double? PassRate, decimal UsdEstimate, int UsdEstimateKnown,
    long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, int Turns, int Continuations, int Checkpointed, int CheckpointedCompleted, PrCounts Prs);

/// <summary>How the governor decided over the period, per signal and rung, and per mode.</summary>
public sealed record GovernorSeries(IReadOnlyList<GovernorCount> Decisions, IReadOnlyList<GovernorModeStats> Modes);

/// <param name="Mode"><c>off</c>, <c>observe</c>, <c>enforce</c>, or <c>none</c> for a record that has no governor.</param>
/// <param name="FalseStopRate">Checkpointed tasks that still completed, over checkpointed tasks; null when none was checkpointed. In <c>observe</c> mode a stop the task did not need, in <c>enforce</c> mode a task that finished after its continuation.</param>
public sealed record GovernorModeStats(string Mode, int Tasks, int Checkpointed, int CheckpointedCompleted, double? FalseStopRate);

/// <summary>Tasks that went on in a fresh session after one was stopped (ADR 0044).</summary>
/// <param name="CompletionRate">Of the tasks with a continuation, the share that completed; null for none.</param>
/// <param name="CompletionRateWithout">The same for tasks with none.</param>
public sealed record ContinuationStats(int TasksWithContinuation, int Continuations, double? CompletionRate, double? CompletionRateWithout);

public sealed record LearningSeriesReport(DateTimeOffset? Since, string Bucket, string GroupBy, int Tasks, IReadOnlyList<SeriesPoint> Points, SeriesPoint Total, GovernorSeries Governor,
    ContinuationStats Continuations);

/// <summary>The learning records (ADR 0045) over time: per day, week or month, optionally per repository, account or size class. Pure: the same entries give the same report. Buckets are UTC.</summary>
public static class LearningSeries
{
    public static readonly IReadOnlyList<string> Buckets = ["day", "week", "month"];

    public static readonly IReadOnlyList<string> Groupings = ["none", "repository", "account", "size"];

    public static LearningSeriesReport Build(IReadOnlyList<LearningEntry> entries, DateTimeOffset? since, string bucket = "day", string groupBy = "none")
    {
        var chosen = entries.Where(e => since is not { } from || e.Record.At >= from).ToList();
        var points = chosen.GroupBy(e => (Start: BucketStart(e.Record.At, bucket), Group: GroupOf(e.Record, groupBy)))
            .OrderBy(g => g.Key.Start).ThenBy(g => g.Key.Group, StringComparer.Ordinal)
            .Select(g => Point(g.Key.Start, g.Key.Group, [.. g]))
            .ToList();
        var total = Point(since ?? chosen.Select(e => e.Record.At).DefaultIfEmpty(DateTimeOffset.UnixEpoch).Min(), null, chosen);

        var decisions = chosen.SelectMany(e => e.Record.Governor).GroupBy(g => (g.Signal, g.Action))
            .OrderBy(g => g.Key.Signal, StringComparer.Ordinal).ThenBy(g => g.Key.Action, StringComparer.Ordinal)
            .Select(g => new GovernorCount(g.Key.Signal, g.Key.Action, g.Sum(c => c.Count), g.Sum(c => c.Acted)))
            .ToList();
        var modes = chosen.GroupBy(e => e.Record.GovernorMode ?? "none").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var checkpointed = g.Where(e => e.Record.Checkpointed).ToList();
            var completed = checkpointed.Count(e => e.Record.Completed);
            return new GovernorModeStats(g.Key, g.Count(), checkpointed.Count, completed, Rate(completed, checkpointed.Count));
        }).ToList();

        var continued = chosen.Where(e => e.Record.Spend.Continuations > 0).ToList();
        var single = chosen.Where(e => e.Record.Spend.Continuations == 0).ToList();
        var continuations = new ContinuationStats(continued.Count, continued.Sum(e => e.Record.Spend.Continuations),
            Rate(continued.Count(e => e.Record.Completed), continued.Count), Rate(single.Count(e => e.Record.Completed), single.Count));
        return new LearningSeriesReport(since, bucket, groupBy, chosen.Count, points, total, new GovernorSeries(decisions, modes), continuations);
    }

    /// <summary>The first instant (UTC) of the day, ISO week (Monday) or month that holds <paramref name="at"/>.</summary>
    public static DateTimeOffset BucketStart(DateTimeOffset at, string bucket)
    {
        var day = at.UtcDateTime.Date;
        return bucket switch
        {
            "week" => new DateTimeOffset(day.AddDays(-(((int)day.DayOfWeek + 6) % 7)), TimeSpan.Zero),
            "month" => new DateTimeOffset(day.Year, day.Month, 1, 0, 0, 0, TimeSpan.Zero),
            _ => new DateTimeOffset(day, TimeSpan.Zero),
        };
    }

    private static string? GroupOf(LearningRecord record, string groupBy) => groupBy switch
    {
        "repository" => record.Repository,
        "account" => record.Actor ?? "owner",
        "size" => record.SizeClass,
        _ => null,
    };

    private static SeriesPoint Point(DateTimeOffset start, string? group, List<LearningEntry> entries)
    {
        var completed = entries.Count(e => e.Record.Completed);
        var checkpointed = entries.Where(e => e.Record.Checkpointed).ToList();
        var withPr = entries.Where(e => e.Record.Pr is not null).ToList();
        var prs = new PrCounts(withPr.Count,
            withPr.Count(e => e.PrOutcome?.Outcome == PrOutcomes.MergedUnchanged), withPr.Count(e => e.PrOutcome?.Outcome == PrOutcomes.MergedEdited),
            withPr.Count(e => e.PrOutcome?.Outcome == PrOutcomes.Merged), withPr.Count(e => e.PrOutcome?.Outcome == PrOutcomes.Closed),
            withPr.Count(e => e.PrOutcome is null || e.PrOutcome.Outcome == PrOutcomes.Open));
        var estimates = entries.Select(e => e.Record.Spend.UsdEstimate).OfType<decimal>().ToList();
        return new SeriesPoint(start, group, entries.Count, completed, entries.Count(e => e.Record.Outcome == LearningOutcome.Partial), entries.Count(e => e.Record.Outcome == LearningOutcome.Failed),
            entries.Count(e => e.Record.Outcome == LearningOutcome.NeedsInput), Rate(completed, entries.Count), estimates.Sum(), estimates.Count,
            entries.Sum(e => e.Record.Spend.InputTokens), entries.Sum(e => e.Record.Spend.OutputTokens), entries.Sum(e => e.Record.Spend.CacheReadTokens), entries.Sum(e => e.Record.Spend.CacheWriteTokens),
            entries.Sum(e => e.Record.Spend.Turns), entries.Sum(e => e.Record.Spend.Continuations), checkpointed.Count, checkpointed.Count(e => e.Record.Completed), prs);
    }

    private static double? Rate(int part, int whole) => whole == 0 ? null : Math.Round((double)part / whole, 3, MidpointRounding.AwayFromZero);

    /// <summary>The <c>since</c> of a request: a number of days (<c>30d</c>) or an ISO 8601 time; null for neither.</summary>
    public static DateTimeOffset? ParseSince(string text, DateTimeOffset now) => LearningReport.ParseSince(text, now);

    internal static string Describe(SeriesPoint p) => string.Create(CultureInfo.InvariantCulture, $"{p.BucketStart:yyyy-MM-dd} {p.Group} {p.Tasks}");
}
