using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Chargehand.Modules.Oversight.Api;

/// <summary>Pull requests of a group by what became of them. <c>Open</c> includes those whose outcome was never read.</summary>
public sealed record PrCounts(int Total, int MergedUnchanged, int MergedEdited, int Merged, int Closed, int Open);

/// <param name="SpendMedianUsd">Nearest-rank median of the tasks whose spend is known; null when none is.</param>
/// <param name="SpendP80Usd">The 80th percentile of the same.</param>
/// <param name="Checkpointed">Tasks with at least one checkpoint decision, acted on or only logged.</param>
/// <param name="CheckpointedCompleted">Of those, the ones that completed: in <c>observe</c> mode a stop the task did not need, in <c>enforce</c> mode a task that finished after its continuation. The false-stop proxy ADR 0045 asks for.</param>
/// <param name="MergedUnchangedRate">Of the pull requests that were merged or closed, the share merged with the head chargehand left; null when none is resolved.</param>
public sealed record LearningGroup(string Repository, string SizeClass, int Tasks, int SpendKnown, decimal? SpendMedianUsd, decimal? SpendP80Usd, int Completed, int Partial,
    double CompletionRate, double PartialRate, int Checkpointed, double CheckpointRate, int CheckpointedCompleted, double? CheckpointedCompletedRate, PrCounts Prs, double? MergedUnchangedRate);

/// <param name="Since">Only tasks that ended at or after this; null: all.</param>
/// <param name="BudgetRefusals">Tasks a gateway's budget refusal ended and merged pull requests per repository over 7 and 30 days before now, whatever <paramref name="Since"/> says
/// (<see cref="Api.BudgetRefusals"/>); null where the caller did not ask for them.</param>
/// <param name="Autonomy">Each repository's earned autonomy (ADR 0097), whatever <paramref name="Since"/> says; null where the caller did not ask for it.</param>
public sealed record LearningSummary(DateTimeOffset? Since, int Tasks, IReadOnlyList<LearningGroup> Groups, IReadOnlyList<BudgetRefusalWindow>? BudgetRefusals = null, IReadOnlyList<AutonomyEntry>? Autonomy = null);

/// <summary>The learning records summed up per repository and size class (ADR 0045), for <c>chargehand learn report</c> and <c>GET /v1/learn</c>. Pure: the same entries give the same report.</summary>
public static partial class LearningReport
{
    private static readonly string[] SizeOrder = ["S", "M", "L"];

    public static LearningSummary Build(IReadOnlyList<LearningEntry> entries, DateTimeOffset? since = null)
    {
        var chosen = entries.Where(e => since is not { } from || e.Record.At >= from).ToList();
        var groups = chosen.GroupBy(e => (e.Record.Repository, e.Record.SizeClass))
            .OrderBy(g => g.Key.Repository, StringComparer.Ordinal).ThenBy(g => Array.IndexOf(SizeOrder, g.Key.SizeClass) is var i and >= 0 ? i : SizeOrder.Length).ThenBy(g => g.Key.SizeClass, StringComparer.Ordinal)
            .Select(g => Group(g.Key.Repository, g.Key.SizeClass, [.. g]));
        return new LearningSummary(since, chosen.Count, [.. groups]);
    }

    private static LearningGroup Group(string repository, string size, IReadOnlyList<LearningEntry> entries)
    {
        var spends = entries.Select(e => e.Record.Spend.UsdEstimate).OfType<decimal>().ToList();
        var completed = entries.Count(e => e.Record.Completed);
        var partial = entries.Count(e => e.Record.Outcome == LearningOutcome.Partial);
        var checkpointed = entries.Where(e => e.Record.Checkpointed).ToList();
        var checkpointedCompleted = checkpointed.Count(e => e.Record.Completed);
        var withPr = entries.Where(e => e.Record.Pr is not null).ToList();
        var prs = new PrCounts(withPr.Count,
            withPr.Count(e => e.PrOutcome?.Outcome == PrOutcomes.MergedUnchanged), withPr.Count(e => e.PrOutcome?.Outcome == PrOutcomes.MergedEdited),
            withPr.Count(e => e.PrOutcome?.Outcome == PrOutcomes.Merged), withPr.Count(e => e.PrOutcome?.Outcome == PrOutcomes.Closed),
            withPr.Count(e => e.PrOutcome is null || e.PrOutcome.Outcome == PrOutcomes.Open));
        var resolved = prs.MergedUnchanged + prs.MergedEdited + prs.Merged + prs.Closed;
        return new LearningGroup(repository, size, entries.Count, spends.Count, LearningStats.Percentile(spends, 50), LearningStats.Percentile(spends, 80), completed, partial,
            Rate(completed, entries.Count)!.Value, Rate(partial, entries.Count)!.Value, checkpointed.Count, Rate(checkpointed.Count, entries.Count)!.Value, checkpointedCompleted,
            Rate(checkpointedCompleted, checkpointed.Count), prs, Rate(prs.MergedUnchanged, resolved));
    }

    private static double? Rate(int part, int whole) => whole == 0 ? null : Math.Round((double)part / whole, 3);

    /// <summary>The table <c>chargehand learn report</c> prints.</summary>
    public static string Render(LearningSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| repository | size | tasks | spend median | spend p80 | completed | partial | checkpointed | completed after checkpoint | pr merged unchanged |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var g in summary.Groups)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {g.Repository} | {g.SizeClass} | {g.Tasks} | {Usd(g.SpendMedianUsd)} | {Usd(g.SpendP80Usd)} | {Share(g.Completed, g.Tasks)} | {Share(g.Partial, g.Tasks)} | {Share(g.Checkpointed, g.Tasks)} | "
                + $"{Share(g.CheckpointedCompleted, g.Checkpointed)} | {Share(g.Prs.MergedUnchanged, g.Prs.MergedUnchanged + g.Prs.MergedEdited + g.Prs.Merged + g.Prs.Closed)} |");
        sb.AppendLine().Append(CultureInfo.InvariantCulture, $"{summary.Tasks} tasks{(summary.Since is { } since ? $" since {since:u}" : "")}. ");
        sb.AppendLine("Spend is the dollar equivalent of the session's tokens (nearest-rank percentiles, tasks with a known spend). "
            + "Checkpointed: tasks with a checkpoint decision, acted on or only logged; completed after checkpoint: the share of those that still completed, ADR 0045's false-stop proxy. "
            + "Pull requests merged unchanged: of those merged or closed, the share merged with the head chargehand left; open ones are not counted.");
        return sb.ToString();
    }

    private static string Usd(decimal? value) => value is { } v ? v.ToString("0.00", CultureInfo.InvariantCulture) : "-";

    private static string Share(int part, int whole) => whole == 0 ? "-" : string.Create(CultureInfo.InvariantCulture, $"{(double)part / whole:0%} ({part}/{whole})");

    /// <summary>A <c>--since</c> or <c>since=</c> value: a number of days (<c>30d</c>) or an ISO 8601 time. Null when it is neither.</summary>
    public static DateTimeOffset? ParseSince(string text, DateTimeOffset now)
    {
        if (Days().Match(text.Trim()) is { Success: true } days)
            return now.AddDays(-int.Parse(days.Groups[1].Value, CultureInfo.InvariantCulture));
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when) ? when : null;
    }

    /// <summary>The median wall seconds of each phase over the last <paramref name="last"/> records that measured them (issue #781), slowest first: <c>chargehand learn phases</c>.</summary>
    public static string RenderPhases(IReadOnlyList<LearningEntry> entries, int last)
    {
        var timed = entries.Select(e => e.Record).Where(r => r.Phases is not null).OrderBy(r => r.At).TakeLast(last).Select(r => r.Phases!).ToList();
        (string Name, Func<LearningPhases, double?> Of)[] phases =
            [("clone", p => p.CloneSeconds), ("restore", p => p.RestoreSeconds), ("model turns", p => p.ModelTurnsSeconds), ("checks", p => p.ChecksSeconds), ("handover", p => p.HandoverSeconds)];
        var rows = phases.Select(p => (p.Name, Values: timed.Select(p.Of).OfType<double>().Select(v => (decimal)v).ToList()))
            .Select(p => (p.Name, p.Values.Count, Median: LearningStats.Percentile(p.Values, 50), P80: LearningStats.Percentile(p.Values, 80)))
            .OrderByDescending(p => p.Median ?? -1).ToList();
        var sb = new StringBuilder().AppendLine("| phase | runs | median s | p80 s |").AppendLine("|---|---|---|---|");
        foreach (var (name, count, median, p80) in rows)
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {count} | {Seconds(median)} | {Seconds(p80)} |");
        return sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"The last {timed.Count} runs with phase times. Nearest-rank percentiles of wall seconds.").ToString();
    }

    private static string Seconds(decimal? value) => value is { } v ? v.ToString("0.0", CultureInfo.InvariantCulture) : "-";

    [GeneratedRegex(@"^(\d{1,5})d$")]
    private static partial Regex Days();
}

/// <summary><c>chargehand learn report [--since 30d|&lt;time&gt;] [--include-bench]</c> (ADR 0045): reads the learning records and prints the table. Benchmark tasks are counted only with <c>--include-bench</c>.
/// Exit 0, 2 on a usage error.</summary>
public static class LearningCli
{
    /// <summary><c>chargehand learn phases [--last 30]</c>: the median wall time per run phase (issue #781). Exit 0, 2 on a usage error.</summary>
    public static async Task<int> PhasesAsync(IReadOnlyList<string> args, ILearningStore store, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var last = 30;
        if (args.Count != 0 && (args is not ["--last", var n] || !int.TryParse(n, CultureInfo.InvariantCulture, out last) || last < 1))
        {
            error.WriteLine("usage: chargehand learn phases [--last <runs>]");
            return 2;
        }
        output.Write(LearningReport.RenderPhases(await store.ReadAsync(ct), last));
        return 0;
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, ILearningStore store, TextWriter output, TextWriter error, DateTimeOffset now, CancellationToken ct)
    {
        DateTimeOffset? since = null;
        var includeBench = false;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--include-bench")
                includeBench = true;
            else if (args[i] == "--since" && i + 1 < args.Count && LearningReport.ParseSince(args[++i], now) is { } parsed)
                since = parsed;
            else
            {
                error.WriteLine("usage: chargehand learn report [--since <days>d|<ISO 8601 time>] [--include-bench]");
                return 2;
            }
        }
        output.Write(LearningReport.Render(LearningReport.Build(await store.ReadAsync(ct, includeBench), since)));
        return 0;
    }
}
