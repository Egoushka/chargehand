using System.Globalization;
using System.Text.Json;
using Chargehand.Modules.Forge.Api;

namespace Chargehand.Modules.Handover.Api;

/// <param name="Partial">The pull request holds unfinished work (ADR 0044): it stays a draft and is never merged.</param>
/// <param name="TaskGoal">The task's goal, for the goal of a fix session.</param>
/// <param name="TestIntegrity">Why the change only edits tests or build files for a goal that did not ask for that (<see cref="TestIntegrityFlag.Reason"/>); the pull request is then never merged, whatever the policy.</param>
/// <param name="LocalOnly">The repository is local-only (ADR 0069): the integrator and the CI fixer do nothing and call no forge.</param>
public sealed record IntegrationTarget(RemoteRepository Repository, int Number, string Branch, bool Partial, string TaskGoal, string? TestIntegrity = null, bool LocalOnly = false);

public enum IntegrationAction { None, Asked, AutoMergeEnabled, Merged, NeedsAttention, Failed }

/// <param name="Detail">One line for the task outcome and the run event.</param>
public sealed record IntegrationResult(IntegrationAction Action, string Detail);

/// <summary>What chargehand does with a pull request after a task's handover opened it (ADR 0046), as the GitHub App (on GitLab, with the forge token), for the repositories the profile lists. <see cref="IntegrateAsync"/> brings the
/// branch up to date, marks the draft ready and merges it by policy; <see cref="FollowAsync"/> then watches the required checks and, when one fails, has a short session fix it, within a fixed number of
/// attempts. Nothing here merges around a rule: a merge is auto-merge (GitHub waits for the rules) or a squash of a pull request GitHub already calls mergeable, pinned to the head commit.</summary>
public sealed partial class Integrator(IntegratorPolicy policy, IIntegrationHost host, DrivenCiFix? ciFix = null, Func<TimeSpan, CancellationToken, Task>? delay = null, DrivenForge? forge = null)
{
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(30);

    /// <summary>The waits after a poll that failed, before the next one; the watch ends when this many polls in a row fail (the last has no wait).</summary>
    public static readonly IReadOnlyList<TimeSpan> PollBackoff = [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120)];

    private static readonly TimeSpan HeadPollEvery = TimeSpan.FromSeconds(5);
    private const int HeadPolls = 6;

    /// <summary>Left in a comment so a repeated pass (a restart, a second batch) does not say the same thing twice.</summary>
    private const string AskMarker = "<!-- chargehand-integration ask -->";

    private const string ConflictMarker = "<!-- chargehand-integration conflict -->";

    private const string TestIntegrityMarker = "<!-- chargehand-integration test-integrity -->";

    private const string MigrationMarker = "<!-- chargehand-integration migration -->";

    private const string NoChecksMarker = "<!-- chargehand-integration no-required-checks -->";

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((span, ct) => Task.Delay(span, ct));

    public DrivenCiFix CiFix { get; } = ciFix ?? new DrivenCiFix();

    public IntegrationMode Mode(RemoteRepository repository) => policy.Resolve(repository);

    /// <summary>The repository of a remote URL on the forge the profile names (<see cref="RemoteRepository.Parse(string, DrivenForge?)"/>).</summary>
    public RemoteRepository? Remote(string url) => RemoteRepository.Parse(url, forge);

    /// <summary>A pull request flagged for test integrity is asked about, never merged, on a repository that would merge.</summary>
    private IntegrationMode ModeFor(IntegrationTarget target) =>
        policy.Resolve(target.Repository) is IntegrationMode.Merge && target.TestIntegrity is not null ? IntegrationMode.Ask : policy.Resolve(target.Repository);

    /// <summary>The sentence the pull request body says about what happens next, for a repository that is opted in; null where the draft-only text is still true (off, or a partial result).</summary>
    public string? Describe(RemoteRepository repository, bool partial) => partial ? null : policy.Resolve(repository) switch
    {
        IntegrationMode.Merge => $"Opened by chargehand as a draft. This repository lets chargehand mark it ready and enable auto-merge, so {host.Name} merges it only once every required check and rule passes; chargehand never bypasses a rule."
            + (CiFix.Attempts > 0 ? $" If a required check fails, chargehand makes up to {CiFix.Attempts.ToString(CultureInfo.InvariantCulture)} attempts to fix it on this branch." : ""),
        IntegrationMode.Ask => "Opened by chargehand as a draft. This repository asks first: chargehand marks it ready for review and does not merge it.",
        _ => null,
    };

    /// <summary>Update the branch if it is behind, mark it ready, then merge or ask. A failure of GitHub is a result, not an exception: the pull request is there either way.
    /// <paramref name="headMoved"/> gets the new head when chargehand's own branch update moved it, so the learning record knows the commits on the branch are still chargehand's (ADR 0045).</summary>
    public async Task<IntegrationResult> IntegrateAsync(IntegrationTarget target, Action<string> report, CancellationToken ct, Action<string>? headMoved = null)
    {
        if (target.LocalOnly)
            return new(IntegrationAction.None, "the repository is local-only: the forge is not called");
        var mode = ModeFor(target);
        if (mode == IntegrationMode.Off)
            return new(IntegrationAction.None, "this repository is not opted in to integration");
        if (target.Partial)
            return Say(report, IntegrationAction.None, "partial result: the pull request stays a draft and is never merged");
        try
        {
            var pr = await host.GetAsync(target.Repository, target.Number, ct);
            if (pr.Merged || pr.Closed)
                return Say(report, IntegrationAction.None, pr.Merged ? "the pull request is already merged" : "the pull request is closed");
            string? migration;
            (mode, migration) = await MigrationModeAsync(target, mode, ct);
            // Auto-merge waits only for required checks: without one, a merge would land before CI runs (CHARGEHAND-244).
            var gate = mode == IntegrationMode.Merge ? await host.ChecksGateAsync(target.Repository, pr.BaseBranch, ct) : ChecksGate.Required;
            if (gate != ChecksGate.Required)
                mode = IntegrationMode.Ask;
            if (mode == IntegrationMode.Merge && await host.BehindByAsync(target.Repository, pr.BaseBranch, target.Branch, ct) > 0)
            {
                var (stopped, moved) = await UpdateBranchAsync(target, pr, report, headMoved, ct);
                if (stopped is not null)
                    return stopped;
                pr = moved!;
            }
            if (pr.IsDraft)
            {
                await host.MarkReadyAsync(target.Repository, pr.NodeId, ct);
                report("marked ready for review");
                pr = await host.GetAsync(target.Repository, target.Number, ct);
            }
            return mode == IntegrationMode.Ask ? await AskAsync(target, pr, gate, migration, report, ct) : await MergeAsync(target, pr, report, ct);
        }
        catch (IntegrationException e)
        {
            return Say(report, IntegrationAction.Failed, $"integration stopped: {e.Message}");
        }
    }

    /// <summary>A pull request that changes a migration is asked about, never merged, on a repository that would merge; the migration's path says why. Files that cannot be read stop the integration.</summary>
    private async Task<(IntegrationMode Mode, string? Migration)> MigrationModeAsync(IntegrationTarget target, IntegrationMode mode, CancellationToken ct)
    {
        if (mode != IntegrationMode.Merge)
            return (mode, null);
        var migration = MigrationPaths.Find(await host.ChangedPathsAsync(target.Repository, target.Number, ct));
        return (migration is null ? mode : IntegrationMode.Ask, migration);
    }

    /// <summary>A branch behind its base brought up to date: the pull request at its new head, or the result that stops the integration.</summary>
    private async Task<(IntegrationResult? Stopped, PrSnapshot? Moved)> UpdateBranchAsync(IntegrationTarget target, PrSnapshot pr, Action<string> report, Action<string>? headMoved, CancellationToken ct)
    {
        try
        {
            await host.UpdateBranchAsync(target.Repository, target.Number, pr.HeadSha, ct);
        }
        catch (IntegrationException e)
        {
            await CommentOnceAsync(target, pr, ConflictMarker, "chargehand could not bring this branch up to date with its base, so it is not merging it; a person needs to resolve it.", ct);
            return (Say(report, IntegrationAction.NeedsAttention, $"the branch is behind its base and could not be updated: {e.Message}"), null);
        }
        var moved = await WaitForHeadAsync(target, pr.HeadSha, ct);
        if (moved is null)
            return (Say(report, IntegrationAction.NeedsAttention, "the branch was behind its base and an update was requested, but its head has not moved yet; not merged"), null);
        report("the branch was behind its base and was updated");
        headMoved?.Invoke(moved.HeadSha);
        return (null, moved);
    }

    private async Task<IntegrationResult> AskAsync(IntegrationTarget target, PrSnapshot pr, ChecksGate gate, string? migration, Action<string> report, CancellationToken ct)
    {
        if (migration is not null)
        {
            await CommentOnceAsync(target, pr, MigrationMarker, $"chargehand marked this pull request ready but does not merge it: it adds a database migration ({migration}). The repository owner merges it.", ct);
            return Say(report, IntegrationAction.Asked, $"marked ready; not merged because it adds a database migration ({migration})");
        }
        if (gate != ChecksGate.Required)
        {
            var reason = gate == ChecksGate.None
                ? $"its base branch {pr.BaseBranch} has no rule that requires a status check, so {host.Name} would merge it before CI runs"
                : $"chargehand could not read the rules of its base branch {pr.BaseBranch}, so it cannot tell whether {host.Name} would wait for CI";
            await CommentOnceAsync(target, pr, NoChecksMarker, $"chargehand marked this pull request ready but does not merge it: {reason}. Add a rule that requires your CI checks on {pr.BaseBranch} and chargehand will merge the next one; a person decides about this one.", ct);
            Console.Error.WriteLine($"chargehand: warning: {target.Repository.Owner}/{target.Repository.Name} is set to merge, but {reason}; treated as ask");
            return Say(report, IntegrationAction.Asked, $"marked ready; not merged because {reason} (treated as ask)");
        }
        if (target.TestIntegrity is { } why)
        {
            await CommentOnceAsync(target, pr, TestIntegrityMarker, $"chargehand marked this pull request ready but does not merge it: {why}. A person decides.", ct);
            return Say(report, IntegrationAction.Asked, $"marked ready; not merged because of test integrity ({why})");
        }
        await CommentOnceAsync(target, pr, AskMarker, "chargehand marked this pull request ready. This repository's policy is \"ask\", so chargehand does not merge it; a person decides.", ct);
        return Say(report, IntegrationAction.Asked, "marked ready; the repository's policy is ask, so it is left for a person to merge");
    }

    private async Task<IntegrationResult> MergeAsync(IntegrationTarget target, PrSnapshot pr, Action<string> report, CancellationToken ct)
    {
        if (pr.MergeStatus == "DIRTY")
        {
            await CommentOnceAsync(target, pr, ConflictMarker, "This pull request conflicts with its base branch, so chargehand is not merging it; a person needs to resolve it.", ct);
            return Say(report, IntegrationAction.NeedsAttention, "the pull request conflicts with its base; not merged");
        }
        if (pr.AutoMerge)
            return Say(report, IntegrationAction.AutoMergeEnabled, "auto-merge was already enabled");
        if (pr.MergeStatus == "CLEAN")
            return await MergeNowAsync(target, pr.HeadSha, report, ct);
        try
        {
            await host.EnableAutoMergeAsync(target.Repository, pr.NodeId, pr.HeadSha, ct);
        }
        catch (IntegrationException)
        {
            // GitHub refuses auto-merge on a pull request that is mergeable already; then the merge itself goes through the same rules.
            var fresh = await host.GetAsync(target.Repository, target.Number, ct);
            if (fresh.IsDraft || fresh.MergeStatus is not ("CLEAN" or "UNSTABLE"))
                throw;
            return await MergeNowAsync(target, fresh.HeadSha, report, ct);
        }
        return Say(report, IntegrationAction.AutoMergeEnabled, $"auto-merge (squash) enabled for {Short(pr.HeadSha)}; {host.Name} merges once the required checks pass");
    }

    /// <summary>Watch the required checks of a pull request that <see cref="IntegrateAsync"/> handled. When some fail and every required check has finished, <paramref name="fix"/> runs a session on the branch,
    /// at most <see cref="DrivenCiFix.Attempts"/> times per pull request and never twice for the same failing jobs (<see cref="CiFixPolicy"/>); the count is read from the pull request's own comments.</summary>
    public async Task<IntegrationResult> FollowAsync(IntegrationTarget target, CiFixSession fix, Action<string> report, CancellationToken ct, Action<string>? headMoved = null, IntegrationWatch? watch = null)
    {
        if (target.LocalOnly)
            return new(IntegrationAction.None, "the repository is local-only: the forge is not called");
        var mode = ModeFor(target);
        if (mode == IntegrationMode.Off || target.Partial)
            return new(IntegrationAction.None, "not watched");
        var maxPolls = Math.Max(1, (int)(TimeSpan.FromMinutes(CiFix.Wait) / PollEvery));
        var polls = 0;
        var failedPolls = 0;
        string? fixedHead = null;
        var gateRead = mode != IntegrationMode.Merge;
        try
        {
            while (true)
            {
                PrSnapshot pr;
                try
                {
                    pr = await host.GetAsync(target.Repository, target.Number, ct);
                }
                catch (IntegrationException e)
                {
                    // A poll that fails (the host could not be reached, a 5xx) says nothing about the checks: wait and ask again, up to PollBackoff.Count failures in a row.
                    if (++failedPolls >= PollBackoff.Count)
                        return Say(report, IntegrationAction.Failed, $"watching the required checks stopped: {failedPolls} polls in a row failed; the last: {e.Message}");
                    report($"poll failed ({failedPolls} of {PollBackoff.Count}): {e.Message}; asking again in {PollBackoff[failedPolls - 1].TotalSeconds:0} s");
                    if (++polls > maxPolls)
                        return Say(report, IntegrationAction.NeedsAttention, $"stopped watching after {CiFix.Wait} minutes: {failedPolls} polls failed and the required checks were not read");
                    await _delay(PollBackoff[failedPolls - 1], ct);
                    continue;
                }
                failedPolls = 0;
                if (Ended(pr, report, watch) is { } ended)
                    return ended;
                // No required check will ever report on a branch without the rule, so there is nothing to wait for.
                if (!gateRead)
                {
                    gateRead = true;
                    if (await host.ChecksGateAsync(target.Repository, pr.BaseBranch, ct) != ChecksGate.Required)
                        return new(IntegrationAction.None, "not watched: the base branch requires no checks");
                }
                var check = Check(pr, fixedHead, watch);
                var step = await ActAsync(target, pr, check, mode, fix, report, headMoved, watch, ct);
                if (step.Stop is { } stop)
                    return stop;
                if (step.Fixed)
                {
                    (fixedHead, polls) = (pr.HeadSha, 0);
                    continue;
                }
                if (++polls > maxPolls)
                    return Say(report, IntegrationAction.NeedsAttention, $"stopped watching after {CiFix.Wait} minutes: " + (check.Decision.Action == CiFixAction.Done ? $"{host.Name} has not merged it yet" : "the required checks have not all finished"));
                await _delay(PollEvery, ct);
            }
        }
        catch (IntegrationException e)
        {
            return Say(report, IntegrationAction.Failed, $"watching the required checks stopped: {e.Message}");
        }
    }

    private static IntegrationResult? Ended(PrSnapshot pr, Action<string> report, IntegrationWatch? watch)
    {
        if (pr.Merged)
        {
            Look(watch, pr, "merged");
            return Say(report, IntegrationAction.Merged, "merged");
        }
        if (pr.Closed)
        {
            Look(watch, pr, "closed");
            return Say(report, IntegrationAction.None, "the pull request was closed");
        }
        return null;
    }

    /// <summary>The required checks of the head and what <see cref="CiFixPolicy"/> decides about them.</summary>
    private FollowCheck Check(PrSnapshot pr, string? fixedHead, IntegrationWatch? watch)
    {
        var required = pr.Checks.Where(c => c.Required).ToList();
        var failing = required.Where(c => c.Outcome == CheckOutcome.Failed).Select(c => c.Name).Distinct().ToList();
        // No required check reported yet is "still waiting": a fresh head has none until its workflows start.
        var pending = pr.HeadSha == fixedHead || required.Count == 0 || required.Any(c => c.Outcome == CheckOutcome.Pending);
        var decision = CiFixPolicy.Decide(FixMarker.Parse(pr.Comments), failing, pending, CiFix.Attempts);
        Look(watch, pr, JsonNamingPolicy.SnakeCaseLower.ConvertName(decision.Action.ToString()));
        return new FollowCheck(required, failing, decision);
    }

    /// <summary>What one look at the checks leads to: a result that ends the watch, a fix that pushed (the watch starts over on the new head), or neither (it polls again).</summary>
    private async Task<FollowStep> ActAsync(IntegrationTarget target, PrSnapshot pr, FollowCheck check, IntegrationMode mode, CiFixSession fix, Action<string> report, Action<string>? headMoved,
        IntegrationWatch? watch, CancellationToken ct)
    {
        var (required, failing, decision) = check;
        if (decision.Action == CiFixAction.Done && required.Any(c => c.Outcome == CheckOutcome.Other))
            return new(Say(report, IntegrationAction.NeedsAttention, "a required check ended without passing or failing (cancelled, or waiting for a person)"), false);
        if (decision.Action == CiFixAction.Done)
            return new(await DoneAsync(target, pr, mode, report, headMoved, ct), false);
        if (decision.Action is CiFixAction.Fix or CiFixAction.GiveUp && CiFix.Attempts == 0)
            return new(Say(report, IntegrationAction.NeedsAttention, $"required checks failed ({string.Join(", ", failing)}); the CI fixer is off"), false);
        if (decision.Action == CiFixAction.GiveUp)
        {
            await CommentOnceAsync(target, pr, FixMarker.GaveUp(decision.JobsKey), $"chargehand is not trying again: {decision.Reason}. A person needs to look at the failing checks.", ct);
            return new(Say(report, IntegrationAction.NeedsAttention, $"CI fix stopped: {decision.Reason}"), false);
        }
        if (decision.Action != CiFixAction.Fix)
            return new(null, false);
        // The failure may not be the pull request's at all (CHARGEHAND-669): a required job that also fails on the base branch's own head is not something a session on this branch can fix.
        if (await BaseFailingAsync(target, pr, failing, ct) is { Count: > 0 } onBase)
        {
            await CommentOnceAsync(target, pr, FixMarker.BaseRed(decision.JobsKey),
                $"chargehand is not starting a CI fix: the required check{(onBase.Count == 1 ? "" : "s")} {string.Join(", ", onBase)} also fail{(onBase.Count == 1 ? "s" : "")} on the base branch {pr.BaseBranch}, so this pull request did not cause it. chargehand will update the branch once the base's required checks pass.", ct);
            return new(Say(report, IntegrationAction.NeedsAttention, $"CI fix skipped: the base branch's required checks are also failing ({string.Join(", ", onBase)})"), false);
        }
        var result = await FixAsync(target, pr, failing, required, decision, fix, report, watch, ct);
        if (!result.Pushed)
            return new(Say(report, IntegrationAction.NeedsAttention, $"CI fix attempt {decision.Attempt} pushed nothing: {result.Detail}"), false);
        report($"CI fix attempt {decision.Attempt} pushed: {result.Detail}");
        return new(null, true);
    }

    /// <summary>Every required check passed: ask leaves it to a person; otherwise a merge chargehand makes or cannot make, or a pull request left for a person, ends the watch, and null waits for the host's auto-merge.</summary>
    private async Task<IntegrationResult?> DoneAsync(IntegrationTarget target, PrSnapshot pr, IntegrationMode mode, Action<string> report, Action<string>? headMoved, CancellationToken ct)
    {
        if (mode == IntegrationMode.Ask)
            return Say(report, IntegrationAction.Asked, "required checks passed; left for a person to merge");
        if (!pr.AutoMerge && await IntegrateAsync(target, report, ct, headMoved) is { Action: IntegrationAction.Asked or IntegrationAction.Merged or IntegrationAction.Failed or IntegrationAction.NeedsAttention } done)
            return done;
        return null;
    }

    private static void Look(IntegrationWatch? watch, PrSnapshot pr, string decision) =>
        watch?.Checks?.Invoke(new CheckObservation(DateTimeOffset.UtcNow, pr.HeadSha, pr.MergeStatus, pr.IsDraft, pr.AutoMerge, pr.Checks, decision));

    private sealed record FollowCheck(List<CheckStatus> Required, List<string> Failing, CiFixDecision Decision);

    /// <param name="Stop">The result that ends the watch; null: it goes on.</param>
    /// <param name="Fixed">A fix session pushed, so the watch starts over on the new head.</param>
    private readonly record struct FollowStep(IntegrationResult? Stop, bool Fixed);

    private async Task<CiFixResult> FixAsync(IntegrationTarget target, PrSnapshot pr, List<string> failing, List<CheckStatus> required, CiFixDecision decision, CiFixSession fix, Action<string> report,
        IntegrationWatch? watch, CancellationToken ct)
    {
        // The marker goes up before the session starts, so an attempt that is cut short still counts.
        await host.CommentAsync(target.Repository, target.Number, $"{FixMarker.Attempt(new FixAttempt(decision.Attempt, decision.JobsKey))}\nchargehand is starting CI fix attempt {decision.Attempt} of {CiFix.Attempts}: {decision.Reason}.", ct);
        report($"CI fix attempt {decision.Attempt} of {CiFix.Attempts} started: {decision.Reason}");
        List<FailedJob> jobs = [];
        foreach (var name in failing)
        {
            string? tail = null;
            if (required.FirstOrDefault(c => c.Name == name && c.JobId is not null)?.JobId is { } jobId)
            {
                try
                {
                    tail = await host.JobLogTailAsync(target.Repository, jobId, CiFixGoal.MaxLogChars, ct);
                }
                catch (IntegrationException)
                {
                    // A log that cannot be read is a goal without it; the session can still run the tests.
                }
            }
            jobs.Add(new FailedJob(name, tail));
        }
        var goal = CiFixGoal.Build(target.TaskGoal, target.Branch, decision.Attempt, CiFix.Attempts, jobs);
        CiFixResult result;
        try
        {
            result = await fix(new CiFixRequest(target, decision.Attempt, CiFix.Attempts, jobs, goal), ct);
        }
        catch (Exception e) when (watch?.Attempt is not null)
        {
            watch.Attempt(new CiAttemptObservation(DateTimeOffset.UtcNow, decision.Attempt, CiFix.Attempts, decision.JobsKey, [.. failing], false, e is OperationCanceledException ? "cancelled" : e.Message));
            throw;
        }
        watch?.Attempt?.Invoke(new CiAttemptObservation(DateTimeOffset.UtcNow, decision.Attempt, CiFix.Attempts, decision.JobsKey, [.. failing], result.Pushed, result.Detail));
        return result;
    }

    private async Task<IntegrationResult> MergeNowAsync(IntegrationTarget target, string headSha, Action<string> report, CancellationToken ct)
    {
        await host.MergeNowAsync(target.Repository, target.Number, headSha, ct);
        return Say(report, IntegrationAction.Merged, $"merged (squash) at {Short(headSha)}");
    }

    /// <summary>The pull request once its head differs from <paramref name="oldHead"/>; null if it has not moved after a short wait (GitHub applies the update after it answers).</summary>
    private async Task<PrSnapshot?> WaitForHeadAsync(IntegrationTarget target, string oldHead, CancellationToken ct)
    {
        for (var i = 0; i < HeadPolls; i++)
        {
            var pr = await host.GetAsync(target.Repository, target.Number, ct);
            if (pr.HeadSha != oldHead)
                return pr;
            await _delay(HeadPollEvery, ct);
        }
        return null;
    }

    private async Task CommentOnceAsync(IntegrationTarget target, PrSnapshot pr, string marker, string text, CancellationToken ct)
    {
        if (pr.Comments.Any(c => c.Contains(marker, StringComparison.Ordinal)))
            return;
        await host.CommentAsync(target.Repository, target.Number, $"{marker}\n{text}", ct);
    }

    private static IntegrationResult Say(Action<string> report, IntegrationAction action, string detail)
    {
        report(detail);
        return new IntegrationResult(action, detail);
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
