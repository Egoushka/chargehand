using System.Diagnostics;
using Chargehand.Contracts;
using Chargehand.Driven;

namespace Chargehand.Server;

/// <param name="RunsHeld">Unfinished top-level runs this process holds (queued or running); for an account, its own.</param>
public sealed record OverviewServer(string Version, bool Halted, int RunsHeld, int RunsQueued, int RunsRunning, int MaxUnfinished, int? MaxConcurrentRuns);

/// <param name="Running">Tasks holding a session slot now: across every batch for the owner, the account's own batches for an account.</param>
/// <param name="MaxParallelTotal"><c>driven.max_parallel_total</c>; owner only.</param>
public sealed record OverviewSessions(int Running, int? MaxParallelTotal, int Batches);

/// <summary>Whether a provider usage or rate limit holds new starts, from the last task that hit one. For an account, only its own batches' pauses.</summary>
public sealed record OverviewRateLimit(bool Paused, DateTimeOffset? ResetsAt, IReadOnlyList<RateLimitBody> Pauses);

public sealed record OverviewRunner(bool Configured, bool? Reachable, string? Error, long? ElapsedMs);

/// <param name="Runner">Owner only.</param>
/// <param name="Host">The host's free memory and load as admission reads them; owner only, null when the runner does not serve <c>/host</c>.</param>
public sealed record OverviewDriven(bool Enabled, OverviewRunner? Runner, HostBody? Host, OverviewSessions Sessions, OverviewRateLimit RateLimit);

/// <summary><c>overview/v1</c>: the console's first screen. <c>Scope</c> is <c>owner</c> or <c>account</c>; an account's body holds its own runs, spend and inbox and nothing about the server's host or the other accounts.</summary>
public sealed record OverviewBody(string ContractVersion, DateTimeOffset GeneratedAt, string Scope, string Account, OverviewServer Server, OverviewDriven Driven, AccountStandingBody Standing,
    IReadOnlyList<AccountBody>? Accounts, InboxCounts Inbox, IReadOnlyList<string> NotRecorded);

internal static class ConsoleOverview
{
    public const string Contract = "overview/v1";

    private static readonly string PauseNote = "The rate-limit pause is the last one a task of a running batch reported; it is held in memory and a restart drops it.";

    private static readonly IReadOnlyList<string> OwnerNotRecorded =
    [
        "Free memory and load are what the runner's host reports right now; the admission rule's memory for a task is the driven preset's, not shown here.",
        PauseNote,
    ];

    private static readonly IReadOnlyList<string> AccountNotRecorded = [PauseNote];

    public static async Task<OverviewBody> BuildAsync(ConsoleContext c, Principal who, CancellationToken ct)
    {
        var standing = await AccountStanding.ReadAsync(who, c.Runs, c.Log, ct);
        var held = c.Runs.Held.Where(r => !r.IsChild && Principal.Sees(who, r.Actor)).ToList();
        var server = new OverviewServer(ConsoleSystem.Version, c.Runs.Halted, held.Count, held.Count(r => r.Latest.Status == RunState.Queued), held.Count(r => r.Latest.Status == RunState.Running), RunService.MaxUnfinished,
            who.IsOwner ? c.Profile?.Http?.MaxConcurrentRuns : null);
        var driven = await DrivenAsync(c, who, ct);
        var accounts = who.IsOwner ? (await ConsoleAccounts.BuildAsync(c, null, null, ct)).Accounts : null;
        var inbox = await ConsoleInbox.CountsAsync(c, who, ct);
        return new OverviewBody(Contract, c.Now, who.IsOwner ? "owner" : "account", who.Name, server, driven, standing, accounts, inbox, who.IsOwner ? OwnerNotRecorded : AccountNotRecorded);
    }

    private static async Task<OverviewDriven> DrivenAsync(ConsoleContext c, Principal who, CancellationToken ct)
    {
        var run = c.Driven;
        var live = run?.LiveBatches().Where(b => Principal.Sees(who, b.Actor)).ToList() ?? [];
        var pauses = (run?.RateLimitPauses(c.Now) ?? []).Where(p => who.IsOwner || live.Any(b => b.RunId == p.BatchRunId)).Select(p => new RateLimitBody(p.BatchRunId, p.TaskId, p.ObservedAt, p.ResetsAt)).ToList();
        var rate = new OverviewRateLimit(pauses.Count > 0, pauses.Count > 0 ? pauses.Max(p => p.ResetsAt) : null, pauses);
        if (run is null || !run.Enabled)
            return new OverviewDriven(false, who.IsOwner ? new OverviewRunner(c.Profile?.Driven?.ResolvedRunners().Count > 0, null, null, null) : null, null, new OverviewSessions(0, who.IsOwner ? run?.MaxParallelTotal : null, 0), rate);
        var running = who.IsOwner ? run.SessionsRunning : live.Sum(b => b.Tasks.Count(t => t.Phase is TaskPhase.Started or TaskPhase.Finished));
        var sessions = new OverviewSessions(running, who.IsOwner ? run.MaxParallelTotal : null, live.Count);
        if (!who.IsOwner)
            return new OverviewDriven(true, null, null, sessions, rate);
        var clock = Stopwatch.StartNew();
        OverviewRunner runner;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            await run.Engine!.CountAsync(limit.Token);
            runner = new OverviewRunner(c.Profile?.Driven?.ResolvedRunners().Count > 0, true, null, clock.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is ChargehandException or OperationCanceledException or HttpRequestException or IOException)
        {
            runner = new OverviewRunner(c.Profile?.Driven?.ResolvedRunners().Count > 0, false, e is OperationCanceledException ? "no answer within 3 s" : e.Message, clock.ElapsedMilliseconds);
        }
        return new OverviewDriven(true, runner, runner.Reachable == true ? await ConsoleSystem.HostAsync(c, ct) : null, sessions, rate);
    }
}
