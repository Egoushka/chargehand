using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Modules.Economics.Api;

/// <summary>One repository and preset over one window. A run's spend is split evenly between the distinct pull requests it names (a batch names one per task), and it counts under that pull request's
/// state now; a run that names none counts under <see cref="NoPullRequestUsd"/>. A batch's cost is split by its task rows.</summary>
/// <param name="Repository"><c>owner/name</c> where the run's repository is a remote, else the last segment of its path; <c>*</c> in a total.</param>
/// <param name="Usd">What the runs cost by their result's usage: a subscription run adds none.</param>
/// <param name="PullRequests">Distinct pull requests the runs opened or served (a review run counts toward the pull request it was sent).</param>
/// <param name="Unknown">Pull requests whose state could not be read: no forge credential and no recorded outcome.</param>
/// <param name="UsdPerMerged">All of <paramref name="Usd"/> over <paramref name="Merged"/>, so the research and review spend of the row is in it; null when nothing merged.</param>
/// <param name="NoPullRequestUsd">Runs that named no pull request: questions, reviews not sent a pull request, tasks that failed before a handover.</param>
/// <param name="ClosedUsd">Runs whose pull requests were closed without a merge.</param>
/// <param name="WastedUsd"><paramref name="NoPullRequestUsd"/> plus <paramref name="ClosedUsd"/>: spend that left no merged change.</param>
public sealed record CostRow(string Repository, string Preset, int Runs, decimal Usd, int PullRequests, int Merged, int ClosedUnmerged, int Open, int Unknown, decimal? UsdPerMerged,
    decimal NoPullRequestUsd, decimal ClosedUsd, decimal WastedUsd);

/// <param name="Total">All repositories and presets of the window.</param>
public sealed record CostWindow(int Days, DateTimeOffset Since, CostRow Total, IReadOnlyList<CostRow> Rows);

/// <summary><c>cost/v1</c>.</summary>
public sealed record CostReport(string ContractVersion, DateTimeOffset GeneratedAt, IReadOnlyList<CostWindow> Windows, IReadOnlyList<string> NotRecorded);

/// <summary>Dollars per merged pull request, per repository and preset (CHARGEHAND-577): the run log's finished top-level runs joined to what became of the pull requests they name. Pure apart from
/// the state lookups: the same log and states give the same report.</summary>
public static class CostReportBuilder
{
    public const string Contract = "cost/v1";

    public static readonly IReadOnlyList<int> DefaultWindows = [7, 30];

    private static readonly IReadOnlyList<string> NotRecorded =
    [
        "A CI fix round's spend is inside the task that ran it, so it counts toward that task's pull request; its attempts are not recorded apart.",
        "A review run counts toward a pull request only when its request carries the pull request's URL as an input source; one that does not counts as spend with no pull request.",
        "Runs are placed in a window by when they finished, and a pull request by its state now.",
    ];

    /// <param name="include">Which runs to count (an account sees its own); null for all.</param>
    public static async Task<CostReport> BuildAsync(RunLogData data, IPullRequestStates states, DateTimeOffset now, Func<StartRecord, bool>? include = null, IReadOnlyList<int>? windows = null,
        CancellationToken ct = default)
    {
        windows ??= DefaultWindows;
        var starts = data.Starts.GroupBy(s => s.RunId).ToDictionary(g => g.Key, g => g.Last());
        var widest = now.AddDays(-windows.Max());
        var runs = data.Runs.GroupBy(r => r.RunId).Select(g => g.Last())
            .Where(r => r.Finished >= widest && starts.TryGetValue(r.RunId, out var s) && s.ParentRunId is null && (include?.Invoke(s) ?? true))
            .Select(r => new Counted(r, Repository(starts[r.RunId]), RunPullRequests.Parts(starts[r.RunId], r))).ToList();

        var known = new Dictionary<PullRequestKey, PullRequestState>();
        await Parallel.ForEachAsync(runs.SelectMany(r => r.Parts).Select(p => p.Pull).OfType<PullRequestKey>().Distinct().ToList(), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (pull, token) =>
        {
            var state = await states.StateOfAsync(pull, token);
            lock (known)
                known[pull] = state;
        });

        return new CostReport(Contract, now, [.. windows.Order().Select(days => Window(days, now, runs.Where(r => r.Run.Finished >= now.AddDays(-days)).ToList(), known))], NotRecorded);
    }

    private static CostWindow Window(int days, DateTimeOffset now, List<Counted> runs, Dictionary<PullRequestKey, PullRequestState> states)
    {
        var rows = runs.GroupBy(r => (r.Repository, r.Run.Preset))
            .Select(g => Row(g.Key.Repository, g.Key.Preset, [.. g], states))
            .OrderByDescending(r => r.Usd).ThenBy(r => r.Repository, StringComparer.Ordinal).ThenBy(r => r.Preset, StringComparer.Ordinal).ToList();
        return new CostWindow(days, now.AddDays(-days), Row("*", "*", runs, states), rows);
    }

    private static CostRow Row(string repository, string preset, List<Counted> runs, Dictionary<PullRequestKey, PullRequestState> states)
    {
        decimal usd = 0, none = 0, closed = 0;
        HashSet<PullRequestKey> pulls = [];
        foreach (var part in runs.SelectMany(r => r.Parts))
        {
            usd += part.Usd;
            if (part.Pull is not { } pull)
                none += part.Usd;
            else
            {
                pulls.Add(pull);
                if (states[pull] == PullRequestState.Closed)
                    closed += part.Usd;
            }
        }

        int Count(PullRequestState state) => pulls.Count(p => states[p] == state);
        var merged = Count(PullRequestState.Merged);
        return new CostRow(repository, preset, runs.Count, usd, pulls.Count, merged, Count(PullRequestState.Closed), Count(PullRequestState.Open), Count(PullRequestState.Unknown),
            merged == 0 ? null : usd / merged, none, closed, none + closed);
    }

    private static string Repository(StartRecord start)
    {
        var path = start.Request.Context.Repository?.Path;
        if (string.IsNullOrWhiteSpace(path))
            return "(none)";
        if (RemoteRepository.Parse(path) is { } remote)
            return $"{remote.Owner}/{remote.Name}";
        var parts = path.Trim().TrimEnd('/', '\\').Split('/', '\\');
        return parts is [var owner, var name] && owner.Length > 0 && !path.StartsWith('/') ? $"{owner}/{name}" : parts[^1];
    }

    private sealed record Counted(RunRecord Run, string Repository, IReadOnlyList<(PullRequestKey? Pull, decimal Usd)> Parts);
}

/// <summary>How a run's spend divides over pull requests: a batch by its task rows (each row's own cost to its pull request, the rest of the batch's cost to none), any other run evenly over the
/// pull requests its <c>pull-request</c> artifact and its request's input sources name, or wholly to none.</summary>
internal static class RunPullRequests
{
    public static IReadOnlyList<(PullRequestKey? Pull, decimal Usd)> Parts(StartRecord start, RunRecord run)
    {
        var usd = run.Result.Usage.Usd ?? 0;
        List<(PullRequestKey? Pull, decimal Usd)> rows = [];
        List<string?> urls = [.. (start.Request.Inputs ?? []).Select(i => i.SourceUrl)];
        foreach (var artifact in run.Result.Artifacts.Where(a => a.Content is not null && a.Kind is "pull-request" or "driven-batch"))
            Read(artifact, rows, urls);

        var keys = urls.Select(PullRequestKey.Parse).OfType<PullRequestKey>().Distinct().ToList();
        var rowsUsd = rows.Sum(r => r.Usd);
        if (rows.Count > 0 && rowsUsd <= usd)
            return [.. rows, .. usd > rowsUsd ? [(null, usd - rowsUsd)] : (IEnumerable<(PullRequestKey?, decimal)>)[]];
        return keys.Count == 0 ? [(null, usd)] : [.. keys.Select(k => ((PullRequestKey?)k, usd / keys.Count))];
    }

    private static void Read(Artifact artifact, List<(PullRequestKey? Pull, decimal Usd)> rows, List<string?> urls)
    {
        try
        {
            using var doc = JsonDocument.Parse(artifact.Content!);
            if (artifact.Kind == "pull-request")
                urls.Add(Text(doc.RootElement, "url"));
            else if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
                foreach (var task in tasks.EnumerateArray())
                    if (task.ValueKind == JsonValueKind.Object && task.TryGetProperty("usd", out var cost) && cost.ValueKind == JsonValueKind.Number)
                        rows.Add((PullRequestKey.Parse(Text(task, "pr_url")), cost.GetDecimal()));
                    else
                        urls.Add(Text(task, "pr_url"));
        }
        catch (JsonException)
        {
            // An artifact that is not JSON names no pull request.
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
