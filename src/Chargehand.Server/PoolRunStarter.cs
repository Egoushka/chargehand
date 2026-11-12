using System.Text;
using Chargehand.Automation;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Pool.Api;

namespace Chargehand.Server;

/// <summary>Starts one pool worker as a driven batch of one task on this server's run service (ADR 0099). The batch is admitted and charged like intake's (<see cref="RunServiceStarter"/>);
/// when it ends, its token use is reported and the worker is released so items enqueued into the scope meanwhile start a successor at the next tick. A batch stopped at the
/// worker's token ceiling hands its items back with a resume card; any other end marks them done (with the status when it did not complete).</summary>
internal sealed class PoolRunStarter(RunService runs, AccountSettings? account, IReadOnlyDictionary<string, AutomationRepository>? repositories, Func<TaskPool> pool,
    TaskPoolSettings settings, Action<string> log) : IPoolStarter
{
    /// <summary>The longest one-line title a pool batch gets; pull request titles are cut shorter anyway.</summary>
    public const int MaxTitle = 72;

    /// <summary>The longest run summary a resume card carries.</summary>
    public const int MaxCardDone = 2000;

    /// <summary>Whether a worker can start on the repository: <c>enqueue</c> refuses the rest, so a queued item never names a scope the starter always refuses.</summary>
    public bool Knows(string repository) => repositories?.ContainsKey(repository) == true;

    public async Task<string?> StartAsync(PoolStartRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (repositories?.GetValueOrDefault(request.Scope.Repository) is not { } repository)
            return $"repository '{request.Scope.Repository}' is not in automation.github.repositories";
        string commit;
        try
        {
            commit = await new GitRepositoryHeads(log).CurrentCommitAsync(repository.Path, ct).ConfigureAwait(false);
        }
        catch (IntakeException e)
        {
            return e.Message;
        }
        var run = new RunRequest("request/v1", $"Pool: {request.Scope.Repository} {request.Scope.Area}.",
            new RequestContext(false, Scheduler.DrivenPreset, Repository: new RepositoryRef(repository.Path, commit)),
            Driven: new RequestDriven([new DrivenTask(request.Worker, Goal: Goal(request), Title: Title(request))], MaxParallel: 1, MaxTokensTotal: settings.WorkerTokens));
        var started = await new RunServiceStarter(runs, account).StartAsync(run, false, ct).ConfigureAwait(false);
        if (started.RunId is not { } id)
            return started.Refusal ?? "the run did not start";
        _ = Task.Run(() => FinishAsync(id, request.Worker, request.Items), CancellationToken.None);
        return null;
    }

    /// <summary>After a restart: each live worker whose batch the run service took up again stays live, and its end is watched as if this process had started it.
    /// Returns those workers; the caller releases the rest as lost.</summary>
    public async Task<IReadOnlySet<string>> AdoptAsync(CancellationToken ct)
    {
        var target = pool();
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (runId, worker) in Resumed(runs.Held, await target.WorkersAsync(ct).ConfigureAwait(false)))
        {
            live.Add(worker.Id);
            var items = (await target.ItemsAsync(worker.Scope, ct).ConfigureAwait(false)).Where(i => i.Worker == worker.Id && i.Status == PoolStatus.Claimed).ToList();
            _ = Task.Run(() => FinishAsync(runId, worker.Id, items), CancellationToken.None);
        }
        return live;
    }

    /// <summary>Each unreleased worker that a resumed batch carries as a task, with that batch's run id; a worker counts once.</summary>
    internal static IReadOnlyList<(string RunId, PoolWorker Worker)> Resumed(IEnumerable<RunHandle> held, IEnumerable<PoolWorker> workers)
    {
        var open = workers.Where(w => w.ReleasedAt is null).ToDictionary(w => w.Id, StringComparer.Ordinal);
        var found = new List<(string, PoolWorker)>();
        foreach (var run in held)
        {
            foreach (var task in run.ResumedTasks)
            {
                if (open.Remove(task, out var worker))
                    found.Add((run.Id, worker));
            }
        }
        return found;
    }

    private async Task FinishAsync(string runId, string worker, IReadOnlyList<PoolItem> items)
    {
        var ct = CancellationToken.None;
        var target = pool();
        try
        {
            var result = runs.Find(runId) is { } handle ? await handle.Done.ConfigureAwait(false) : null;
            if (result is not null)
                await target.ReportAsync(worker, result.Usage.Input + result.Usage.Output, ct).ConfigureAwait(false);
            if (Card(result, settings.WorkerTokens) is { } card)
            {
                // The items are still claimed: the release puts them back in the queue and the card goes to the successor.
                await target.ReleaseAsync(worker, card, ct).ConfigureAwait(false);
                return;
            }
            var reason = result is null ? "failed: lost" : result.Status == ResultStatus.Completed ? null : "failed: " + result.Status.ToString().ToLowerInvariant();
            await target.DoneAsync([.. items.Select(i => i.Key)], reason, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log($"pool: worker {worker} did not finish cleanly: {e.GetType().Name}");
        }
        finally
        {
            await ReleaseQuietlyAsync(target, worker).ConfigureAwait(false);
        }
    }

    private async Task ReleaseQuietlyAsync(TaskPool target, string worker)
    {
        try
        {
            await target.ReleaseAsync(worker, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log($"pool: worker {worker} could not be released: {e.GetType().Name}");
        }
    }

    /// <summary>The card a batch stopped at its token ceiling leaves; null for any other end. A batch that used no tokens made no progress, so handing its items back
    /// would only start the same stop again: those items end as failed instead.</summary>
    internal static ResumeCard? Card(ResultContract? result, long ceiling)
    {
        if (result is not { Status: ResultStatus.Failed, Error.Code: ErrorCode.CostCapReached } || result.Usage.Input + result.Usage.Output <= 0)
            return null;
        var done = string.IsNullOrWhiteSpace(result.Summary) ? "(the run left no summary)" : result.Summary.Trim();
        if (done.Length > MaxCardDone)
            done = done[..(MaxCardDone - 1)] + "…";
        return new ResumeCard(done, "Every item below; check each against the repository before redoing it.", $"The worker's token ceiling of {ceiling} tokens.");
    }

    internal static string Title(PoolStartRequest request)
    {
        var first = request.Items.Count > 0 ? request.Items[0].Title : request.Scope.Area;
        var title = request.Items.Count > 1 ? $"{request.Scope.Area}: {first} (+{request.Items.Count - 1})" : first;
        title = title.ReplaceLineEndings(" ");
        return title.Length <= MaxTitle ? title : title[..(MaxTitle - 1)] + "…";
    }

    internal static string Goal(PoolStartRequest request)
    {
        var goal = new StringBuilder();
        goal.Append("Work through these items of the area '").Append(request.Scope.Area).Append("' in order, in one branch. Each item is a task from the owner's queue.\n");
        if (request.Card is { } card)
        {
            goal.Append("\nAn earlier worker on this area stopped at its token ceiling. Its card:\n");
            goal.Append("- Done: ").Append(card.Done).Append('\n');
            goal.Append("- Left: ").Append(card.Left).Append('\n');
            goal.Append("- Stopped: ").Append(card.Stopped).Append('\n');
        }
        for (var i = 0; i < request.Items.Count; i++)
        {
            var item = request.Items[i];
            goal.Append("\n## ").Append(i + 1).Append(". ").Append(item.Title).Append('\n');
            if (item.PullRequest is { } pr)
                goal.Append("Pull request #").Append(pr).Append('\n');
            goal.Append(item.Body).Append('\n');
        }
        return goal.ToString();
    }
}
