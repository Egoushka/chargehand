using System.Globalization;
using System.Text;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Economics.Api;

/// <summary><c>chargehand cost [--json]</c>: dollars per merged pull request, per repository and preset over 7 and 30 days. It reads the run log and the pull request outcomes the server recorded
/// (no credential, no network), so a pull request of a run the server never tracked reads as unknown; the console asks the forge. Exit 0, 2 on a usage error.</summary>
public static class CostCli
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, IRunLog log, ILearningStore learning, TextWriter output, TextWriter error, DateTimeOffset now, Func<CostReport, string> json,
        CancellationToken ct)
    {
        if (args.Count > 1 || args.Count == 1 && args[0] != "--json")
        {
            error.WriteLine("usage: chargehand cost [--json]");
            return 2;
        }

        var states = new RecordedPullRequestStates(await learning.ReadAsync(ct));
        var report = await CostReportBuilder.BuildAsync(await log.ReadAllAsync(ct), states, now, ct: ct);
        output.Write(args.Count == 1 ? json(report) + "\n" : Render(report));
        return 0;
    }

    public static string Render(CostReport report)
    {
        var text = new StringBuilder();
        foreach (var window in report.Windows)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"last {window.Days} days");
            text.AppendLine("repository | preset | runs | usd | prs | merged | closed | open | unknown | usd/merged | no-pr usd | closed usd | wasted usd");
            foreach (var r in new[] { window.Total }.Concat(window.Rows))
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"{r.Repository} | {r.Preset} | {r.Runs} | {r.Usd:F2} | {r.PullRequests} | {r.Merged} | {r.ClosedUnmerged} | {r.Open} | {r.Unknown} | {(r.UsdPerMerged is { } m ? m.ToString("F2", CultureInfo.InvariantCulture) : "-")} | {r.NoPullRequestUsd:F2} | {r.ClosedUsd:F2} | {r.WastedUsd:F2}");
            text.AppendLine();
        }

        foreach (var note in report.NotRecorded)
            text.AppendLine(CultureInfo.InvariantCulture, $"note: {note}");
        return text.ToString();
    }
}
