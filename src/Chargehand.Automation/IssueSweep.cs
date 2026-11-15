using Chargehand.Modules.Events.Api;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Pool.Api;

namespace Chargehand.Automation;

/// <summary>One pass over every watched repository: sync labels, read the open issues, filter, check blockers, sort (with a cache), label, comment on a changed verdict and enqueue what is ready.</summary>
/// <param name="trustedAuthors">The authors whose issues the sweep takes: <c>allowed_authors</c> and the integrator App's login.</param>
public sealed partial class IssueSweep(
    SweepSettings settings,
    IReadOnlyDictionary<string, AutomationRepository> repositories,
    IIntakeHost host,
    ISweepSorter sorter,
    SweepState state,
    TaskPool pool,
    IEventLog events,
    IntakeState intake,
    IRepositoryHeads heads,
    IReadOnlySet<string> trustedAuthors,
    IEpicSplitter splitter,
    TimeProvider clock)
{
    public const string CommentMarker = "<!-- chargehand-sweep -->";
    public static string NewId() => $"sweep-{Guid.NewGuid():N}";

    public const string SplitMarker = "<!-- chargehand-split -->";

    /// <param name="trigger"><c>schedule</c> or <c>manual</c>.</param>
    /// <param name="id">The id the caller already gave this sweep; null: a new one.</param>
    public async Task<SweepReport> RunAsync(string trigger, CancellationToken ct, string? id = null)
    {
        id ??= NewId();
        var started = clock.GetUtcNow();
        await EmitAsync(id, "chargehand.sweep.started", "started", w => w.WriteString("trigger", trigger), ct);
        var budget = new Budget(settings.MaxSortsPerSweep);
        var rows = new List<SweepRepoRow>();
        var needsYou = new List<SweepNeedsYou>();
        foreach (var (repository, checkout) in repositories.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            var (row, items) = await SweepOneAsync(id, repository, checkout, budget, ct);
            rows.Add(row);
            needsYou.AddRange(items);
        }

        var report = new SweepReport(id, trigger, started, clock.GetUtcNow(), rows, needsYou);
        await EmitAsync(id, "chargehand.sweep.finished", "finished", w => WriteCounts(w, report), ct);
        return report;
    }

    private async Task<(SweepRepoRow Row, IReadOnlyList<SweepNeedsYou> NeedsYou)> SweepOneAsync(string sweepId, string repository, AutomationRepository checkout, Budget budget, CancellationToken ct)
    {
        var tally = new Tally();
        try
        {
            await SweepRepositoryAsync(repository, checkout, budget, tally, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            tally.Problem ??= ProblemLine(ex);
        }

        if (tally.Problem is { } problem)
        {
            await EmitAsync(sweepId, "chargehand.sweep.problem", $"problem-{repository.Replace('/', '-')}", w =>
            {
                w.WriteString("repository", repository);
                w.WriteString("text", problem);
            }, ct);
        }

        return (tally.Row(repository, tally.Problem), tally.NeedsYouItems);
    }

    private async Task SweepRepositoryAsync(string repository, AutomationRepository checkout, Budget budget, Tally tally, CancellationToken ct)
    {
        foreach (var label in SweepLabels.All)
            await host.SyncLabelAsync(repository, label, ct);
        var issues = await host.OpenIssuesAsync(repository, ct);
        var chargehandPulls = await ChargehandPullIssuesAsync(repository, ct);
        var context = new RepositoryContext(repository, checkout, chargehandPulls);
        foreach (var issue in issues.Where(i => !i.IsPullRequest).OrderBy(i => i.UpdatedAt))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await SweepIssueAsync(context, issue, budget, tally, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                tally.Problem ??= ProblemLine(ex);
            }
        }
    }

    private async Task SweepIssueAsync(RepositoryContext context, IntakeIssue issue, Budget budget, Tally tally, CancellationToken ct)
    {
        var repository = context.Repository;
        var queued = await QueuedOrRunningAsync(context, issue.Number, ct);
        var skip = SweepFilter.Skip(issue, false, queued, trustedAuthors);
        if (skip == SweepSkip.None && await heads.ClaimAsync(context.Checkout.Path, issue.Number, ct) is { } claim && claim.ExpiresAt > clock.GetUtcNow())
            skip = SweepSkip.Claimed;
        if (skip != SweepSkip.None)
        {
            tally.Skipped++;
            return;
        }

        var key = $"{repository}#{issue.Number}";
        var hash = SweepState.Hash(issue);
        var (open, unreadable) = await BlockersAsync(repository, issue, ct);
        var cacheHash = hash;
        SortResult result;
        if (open is not null)
        {
            result = new SortResult(SweepVerdict.Blocked, "", $"Blocked by {open}, which is still open.");
            cacheHash = $"blocked:{hash}";
        }
        else if (await state.CachedAsync(key, hash) is { } cached)
            result = cached;
        else if (budget.TryTake())
            result = await sorter.SortAsync(issue, repository, context.ClaudeMd(), ct);
        else
        {
            tally.Deferred++;
            return;
        }

        // An answer that did not fit the schema is a blip, not a verdict: nothing is written or cached, and the next sweep sorts again.
        if (result == ModelSweepSorter.Unsorted)
        {
            tally.Deferred++;
            return;
        }

        // The pool item for this exact text ended and the issue is still open with no pull request: queuing it again would loop. Editing the issue changes the hash.
        if (result.Verdict == SweepVerdict.Ready && await state.QueuedHashAsync(key) == hash)
            result = new SortResult(SweepVerdict.NeedsOwner, result.Size, AlreadyTried);

        await PublishAsync(repository, issue, key, cacheHash, result, unreadable, ct);
        tally.Count(repository, issue, result);
        if (result.Verdict == SweepVerdict.Epic && !issue.Body.Contains(SplitMarker, StringComparison.Ordinal))
            await splitter.SplitAsync(repository, issue, ct);
        else if (result.Verdict == SweepVerdict.Ready && !await QueuedOrRunningAsync(context, issue.Number, ct) && await EnqueueAsync(repository, issue, result, ct))
        {
            await state.MarkQueuedAsync(key, hash);
            tally.Enqueued++;
        }
    }

    private async Task PublishAsync(string repository, IntakeIssue issue, string key, string cacheHash, SortResult result, IReadOnlyList<string> unreadable, CancellationToken ct)
    {
        var (add, remove) = LabelChanges(issue, result);
        if (add.Count > 0 || remove.Count > 0)
            await host.SetLabelsAsync(repository, issue.Number, add, remove, ct);
        if (ShouldComment(issue, result.Verdict, await state.LastVerdictAsync(key)))
            await host.CommentAsync(repository, issue.Number, CommentText(result, unreadable), ct);
        await state.PutAsync(key, cacheHash, result);
    }

    private async Task<bool> EnqueueAsync(string repository, IntakeIssue issue, SortResult result, CancellationToken ct)
    {
        var priority = Has(issue, "priority:urgent") ? 2 : Has(issue, "priority:high") ? 1 : 0;
        var title = issue.Title.Length > 200 ? issue.Title[..200] : issue.Title;
        var body = issue.Body.Length > MaxBodyChars ? issue.Body[..MaxBodyChars] : issue.Body;
        var queued = await pool.EnqueueAsync(new EnqueueRequest(repository, $"issue-{issue.Number}", title, body, priority, null, "sweep", issue.Number, result.Size), ct);
        return queued.Item is not null;
    }

    /// <summary>The reason given when the issue's text already went through the pool once and is still open.</summary>
    internal const string AlreadyTried = "chargehand already worked on this issue as written and it is still open; edit it to have chargehand try again";

    private static bool Has(IntakeIssue issue, string label) => issue.Labels.Contains(label, StringComparer.OrdinalIgnoreCase);

    private sealed class Budget(int left)
    {
        public bool TryTake() => left > 0 && left-- > 0;
    }
}
