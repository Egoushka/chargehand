using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Server;

/// <summary>Finished top-level runs (a batch counts as one; its tasks and a session's calls do not) in one bucket, for one group. <c>Usd</c> is the priced cost: a subscription run adds none.</summary>
public sealed record RunPoint(DateTimeOffset BucketStart, string? Group, int Runs, int Completed, int Failed, int NeedsInput, int Denied, decimal Usd, long Tokens);

/// <summary><c>analytics/v1</c>: finished driven tasks over time (from the learning records) and finished runs over time (from the run log).</summary>
/// <param name="Tasks">Spend, tokens (cache included), pass rate, continuations and the governor's false-stop proxy per bucket and group.</param>
public sealed record AnalyticsBody(string ContractVersion, DateTimeOffset GeneratedAt, DateTimeOffset? Since, string Bucket, string GroupBy, string? Account, LearningSeriesReport Tasks,
    IReadOnlyList<RunPoint> Runs, IReadOnlyList<string> NotRecorded);

internal static class ConsoleAnalytics
{
    public const string Contract = "analytics/v1";

    private static readonly IReadOnlyList<string> NotRecorded =
    [
        "The CI fixer's attempts and cost are not recorded apart: a task's spend includes its fix sessions, and the attempts are only in the live integrated events.",
        "Tasks that threw before their session ended (cancelled, a start that failed) write no learning record, so they are not in 'tasks'.",
        "'runs' counts finished top-level runs by their result's usage; a run still going, and the calls inside a batch, are left out.",
    ];

    /// <param name="account">The owner may narrow to one account (<c>owner</c> for runs with no account); an account always gets its own.</param>
    public static async Task<AnalyticsBody> BuildAsync(ConsoleContext c, Principal? who, DateTimeOffset? since, string bucket, string groupBy, string? account, CancellationToken ct)
    {
        var scope = who is { IsOwner: false } ? who.Name : account;
        bool InScope(string? actor) => Principal.Sees(who, actor) && (scope is null || (actor ?? AccountSettings.Owner) == scope);
        var entries = (await c.LearningAsync(ct)).Where(e => InScope(e.Record.Actor)).ToList();
        var tasks = LearningSeries.Build(entries, since, bucket, groupBy);
        var index = await c.IndexAsync(ct);
        var runs = index.Finished.Values
            .Where(r => (since is not { } from || r.Finished >= from) && index.Starts.TryGetValue(r.RunId, out var s) && s.ParentRunId is null && InScope(s.Actor ?? r.Actor))
            .GroupBy(r => (Start: LearningSeries.BucketStart(r.Finished, bucket), Group: RunGroup(index.Starts[r.RunId], r, groupBy)))
            .OrderBy(g => g.Key.Start).ThenBy(g => g.Key.Group, StringComparer.Ordinal)
            .Select(g => new RunPoint(g.Key.Start, g.Key.Group, g.Count(), g.Count(r => r.Result.Status == ResultStatus.Completed), g.Count(r => r.Result.Status == ResultStatus.Failed),
                g.Count(r => r.Result.Status == ResultStatus.NeedsInput), g.Count(r => r.Result.Status == ResultStatus.Denied), g.Sum(r => r.Result.Usage.Usd ?? 0), g.Sum(r => r.Result.Usage.Input + r.Result.Usage.Output)))
            .ToList();
        return new AnalyticsBody(Contract, c.Now, since, bucket, groupBy, scope, tasks, runs, NotRecorded);
    }

    /// <summary>Runs group by account or repository; there is no size class on a run.</summary>
    private static string? RunGroup(Chargehand.Modules.Ledger.Api.StartRecord start, Chargehand.Modules.Ledger.Api.RunRecord run, string groupBy) => groupBy switch
    {
        "account" => start.Actor ?? run.Actor ?? AccountSettings.Owner,
        "repository" => start.Request.Context.Repository?.Path,
        _ => null,
    };
}
