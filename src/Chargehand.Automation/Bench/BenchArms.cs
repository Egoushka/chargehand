using System.Globalization;
using System.Text;

namespace Chargehand.Bench;

/// <summary>The two arms of a benchmark side by side (ADR 0079): chargehand's driven session and the bare agent, on the same task set. Rates carry the Wilson interval; the per-task table and
/// the list of tasks where the arms differ come from the same facts as every other report.</summary>
public static class BenchArms
{
    public const string ArmDefinition = "A task passes when the agent left a branch and its acceptance check exits 0 on it. In the bare arm that is all: no tests are run on the branch before the check, and no pull request is opened. "
        + "An honest stop is an impossible task that left no commits, so its protected files are unchanged and nothing was claimed done. The bare arm cannot tell a branch that says the task is impossible from one that claims it is done, "
        + "so any commit on an impossible task counts as done (false done), the stricter reading.";

    /// <summary>The report of a chargehand file and a bare file. The order of the arguments does not matter; chargehand is the left column.</summary>
    public static string Compare(BenchResults first, BenchResults second)
    {
        var (c, b) = first.ArmName == BenchResults.ChargehandArm ? (first, second) : (second, first);
        var (sc, sb) = (BenchStats.Compute(c), BenchStats.Compute(b));
        var o = new StringBuilder();
        o.AppendLine(CultureInfo.InvariantCulture, $"# Benchmark arms: chargehand against {b.ArmName}, {c.Taskset}");
        o.AppendLine();
        o.AppendLine(CultureInfo.InvariantCulture, $"chargehand: started {c.StartedAt:yyyy-MM-dd HH:mm} UTC, {sc.Tasks} tasks. {b.ArmName}: started {b.StartedAt:yyyy-MM-dd HH:mm} UTC, {sb.Tasks} tasks.");
        o.AppendLine();
        foreach (var warning in BenchReport.Warnings(c, b))
            o.AppendLine(CultureInfo.InvariantCulture, $"> **{warning}**");
        o.AppendLine();
        o.AppendLine("| | chargehand | bare | Difference |");
        o.AppendLine("|---|---|---|---|");
        Row(o, "Pass rate (tasks that can be done)", sc.Pass, sb.Pass);
        Row(o, "Honest stops (tasks that cannot be done)", sc.HonestStop, sb.HonestStop);
        Count(o, "Impossible tasks called done, or whose protected files changed", sc.FalseDone, sb.FalseDone);
        Count(o, "Impossible tasks that ended on a limit or a fault", sc.Aborted, sb.Aborted);
        Number(o, "Tokens per task (input plus output)", sc.MeanTokens, sb.MeanTokens, "N0");
        Number(o, "Dollar equivalent per task", (double?)sc.MeanUsd, (double?)sb.MeanUsd, "0.00", "$");
        Number(o, "Dollar equivalent per pass", (double?)sc.UsdPerPass, (double?)sb.UsdPerPass, "0.00", "$");
        Number(o, "Turns per task (mean)", sc.MeanTurns, sb.MeanTurns, "0.#");
        o.AppendLine();
        o.AppendLine(sc.Pass.Total > 0 && sb.Pass.Total > 0 && sc.Pass.Overlaps(sb.Pass)
            ? "The pass-rate intervals overlap: with this many tasks the difference could be chance. One task moves a rate by several points."
            : "The pass-rate intervals do not overlap.");
        o.AppendLine();
        o.AppendLine(ArmDefinition);
        o.AppendLine();
        o.AppendLine("The dollar figure is the session's tokens at the profile's price table, not a bill; it is blank when the profile has no prices. Question tasks get no second round in the bare arm: it has no way to ask and is not given the answers.");
        o.AppendLine();
        Tasks(o, c, b);
        return o.ToString();
    }

    private static void Tasks(StringBuilder o, BenchResults c, BenchResults b)
    {
        var both = c.Tasks.Join(b.Tasks, t => t.Id, t => t.Id, (x, y) => (Chargehand: x, Bare: y)).ToList();
        o.AppendLine("## Per task");
        o.AppendLine();
        o.AppendLine("| Task | Kind | Size | chargehand | bare | Tokens chargehand | Tokens bare | Dollar equivalent chargehand | Dollar equivalent bare |");
        o.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var (x, y) in both)
            o.AppendLine(CultureInfo.InvariantCulture, $"| {x.Id} | {x.Kind} | {x.Size} | {BenchStats.OutcomeOf(x)} | {BenchStats.OutcomeOf(y)} | {x.Tokens:N0} | {y.Tokens:N0} | {Money(x.UsdEstimate)} | {Money(y.UsdEstimate)} |");
        o.AppendLine();
        var differ = both.Where(p => BenchStats.OutcomeOf(p.Chargehand) != BenchStats.OutcomeOf(p.Bare)).ToList();
        o.AppendLine(CultureInfo.InvariantCulture, $"## Tasks where the arms differ ({differ.Count} of {both.Count} in both)");
        o.AppendLine();
        if (differ.Count > 0)
        {
            o.AppendLine("| Task | chargehand | bare | Tokens chargehand | Tokens bare |");
            o.AppendLine("|---|---|---|---|---|");
            foreach (var (x, y) in differ)
                o.AppendLine(CultureInfo.InvariantCulture, $"| {x.Id} | {BenchStats.OutcomeOf(x)} | {BenchStats.OutcomeOf(y)} | {x.Tokens:N0} | {y.Tokens:N0} |");
            o.AppendLine();
        }
        var onlyC = c.Tasks.Select(t => t.Id).Except(b.Tasks.Select(t => t.Id)).ToList();
        var onlyB = b.Tasks.Select(t => t.Id).Except(c.Tasks.Select(t => t.Id)).ToList();
        if (onlyC.Count + onlyB.Count > 0)
            o.AppendLine(CultureInfo.InvariantCulture, $"Only in chargehand: {(onlyC.Count == 0 ? "none" : string.Join(", ", onlyC))}. Only in bare: {(onlyB.Count == 0 ? "none" : string.Join(", ", onlyB))}.");
    }

    private static void Row(StringBuilder o, string name, Rate chargehand, Rate bare)
    {
        var change = chargehand.Value is { } x && bare.Value is { } y ? string.Create(CultureInfo.InvariantCulture, $"{(y - x) * 100:+0;-0;0} points") : "n/a";
        o.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {chargehand.Format()} | {bare.Format()} | {change} |");
    }

    private static void Count(StringBuilder o, string name, int chargehand, int bare) =>
        o.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {chargehand} | {bare} | {bare - chargehand:+0;-0;0} |");

    private static void Number(StringBuilder o, string name, double? chargehand, double? bare, string format, string prefix = "")
    {
        string F(double? v) => v is { } n ? prefix + n.ToString(format, CultureInfo.InvariantCulture) : "n/a";
        var change = chargehand is { } x && bare is { } y && x != 0 ? string.Create(CultureInfo.InvariantCulture, $"{(y - x) / x * 100:+0;-0;0}%") : "n/a";
        o.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {F(chargehand)} | {F(bare)} | {change} |");
    }

    private static string Money(decimal? usd) => usd is { } v ? string.Create(CultureInfo.InvariantCulture, $"${v:0.00}") : "n/a";
}
