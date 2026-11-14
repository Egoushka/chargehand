using System.Globalization;
using System.Text;

namespace Chargehand.Modules.Oversight.Api;

/// <summary>A candidate rule set to replay (issue 436). The spend rule stops a task once it spent more than <paramref name="BudgetFactor"/> times its recorded budget; a signal in
/// <paramref name="StopSignals"/> stops it when the log shows that signal reached a checkpoint or a stop.</summary>
public sealed record ReplayPolicy(decimal BudgetFactor = 1m, IReadOnlyList<string>? StopSignals = null);

/// <param name="Stops">Tasks the policy would have stopped on this signal; a task that two signals stop counts once in each and once in the total.</param>
/// <param name="FalseStops">Of those, the tasks that completed anyway.</param>
/// <param name="SpendAfterStopUsd">What the stopped tasks spent after the stop point; known for the spend rule only: a signal's record holds a count, not the spend at its turn.</param>
public sealed record ReplayRow(string Key, int Stops, int FalseStops, decimal SpendAfterStopUsd)
{
    public double? FalseStopRate => Stops == 0 ? null : (double)FalseStops / Stops;
}

/// <param name="Tasks">Tasks replayed.</param>
/// <param name="Total">Tasks stopped by any rule, each once.</param>
public sealed record ReplayReport(ReplayPolicy Policy, int Tasks, ReplayRow Total, IReadOnlyList<ReplayRow> BySignal, IReadOnlyList<ReplayRow> BySize);

/// <summary>Evaluates a candidate governor policy against recorded tasks (ADR 0045: enforce once false stops stay under 5%). Pure: the same entries and policy give the same report.</summary>
public static class GovernorReplay
{
    public const string SpendSignal = "spend";

    private static readonly string[] SizeOrder = ["S", "M", "L"];

    public static ReplayReport Build(IReadOnlyList<LearningEntry> entries, ReplayPolicy policy, DateTimeOffset? since = null)
    {
        var tasks = entries.Select(e => e.Record).Where(r => since is null || r.At >= since).ToList();
        var stopped = tasks.Select(r => (Record: r, Stops: StopsOf(r, policy))).Where(t => t.Stops.Count > 0).ToList();
        var bySignal = stopped.SelectMany(t => t.Stops.Select(s => (t.Record, Signal: s.Key, s.Value)))
            .GroupBy(t => t.Signal).OrderBy(g => g.Key == SpendSignal ? "" : g.Key, StringComparer.Ordinal)
            .Select(g => Row(g.Key, g.Select(t => (t.Record, t.Value)))).ToList();
        var bySize = stopped.GroupBy(t => t.Record.SizeClass).OrderBy(g => Array.IndexOf(SizeOrder, g.Key) is var i and >= 0 ? i : SizeOrder.Length).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => Row(g.Key, g.Select(t => (t.Record, t.Stops.Values.Sum())))).ToList();
        return new ReplayReport(policy, tasks.Count, Row("all", stopped.Select(t => (t.Record, t.Stops.Values.Sum()))), bySignal, bySize);
    }

    private static ReplayRow Row(string key, IEnumerable<(LearningRecord Record, decimal Spend)> items)
    {
        var list = items.ToList();
        return new ReplayRow(key, list.Count, list.Count(i => i.Record.Completed), list.Sum(i => i.Spend));
    }

    /// <summary>The signals that stop the task under the policy, each with the spend after its stop point (zero when unknown).</summary>
    private static Dictionary<string, decimal> StopsOf(LearningRecord r, ReplayPolicy policy)
    {
        var stops = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (r.Budget.Usd is { } budget && r.Spend.UsdEstimate is { } spent && spent > budget * policy.BudgetFactor)
            stops[SpendSignal] = spent - budget * policy.BudgetFactor;
        foreach (var g in r.Governor)
            if (g.Count > 0 && g.Action is "checkpoint" or "stop" && g.Signal != SpendSignal && policy.StopSignals?.Contains(g.Signal) == true)
                stops.TryAdd(g.Signal, 0m);
        return stops;
    }

    public static string Render(ReplayReport report)
    {
        var text = new StringBuilder();
        var signals = report.Policy.StopSignals is { Count: > 0 } s ? string.Join(", ", s) : "none";
        text.Append(CultureInfo.InvariantCulture, $"Governor replay: spend stop at {report.Policy.BudgetFactor:0.##} x the recorded budget; signals that stop: {signals}.\n");
        text.Append(CultureInfo.InvariantCulture, $"{report.Tasks} tasks replayed, {report.Total.Stops} stopped, {report.Total.FalseStops} of them completed anyway ({Rate(report.Total)}; ADR 0045 enforces under 5%).\n\n");
        Table(text, "signal", report.BySignal);
        Table(text, "size", report.BySize);
        text.Append("Spend after the stop counts the spend rule only; a signal's record has no spend at its turn. A task two signals stop counts once in each signal row.\n");
        return text.ToString();
    }

    private static void Table(StringBuilder text, string label, IReadOnlyList<ReplayRow> rows)
    {
        text.Append(CultureInfo.InvariantCulture, $"| {label} | stops | false stops | false-stop rate | spend after stop |\n|---|---|---|---|---|\n");
        foreach (var r in rows)
            text.Append(CultureInfo.InvariantCulture, $"| {r.Key} | {r.Stops} | {r.FalseStops} | {Rate(r)} | ${r.SpendAfterStopUsd:0.00} |\n");
        text.Append('\n');
    }

    private static string Rate(ReplayRow r) => r.FalseStopRate is { } x ? (x * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%" : "n/a";
}
