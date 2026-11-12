using System.Text;
using Chargehand.Automation;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Pool.Api;

namespace Chargehand.Server;

/// <summary>Starts one pool worker as a driven batch of one task on this server's run service (ADR 0099). The batch is admitted and charged like intake's (<see cref="RunServiceStarter"/>);
/// when it ends, its token use is reported, the items it was given are marked done (with the status when it did not complete), and the worker is released so items enqueued
/// into the scope meanwhile start a successor at the next tick.</summary>
internal sealed class PoolRunStarter(RunService runs, AccountSettings? account, IReadOnlyDictionary<string, AutomationRepository>? repositories, Func<TaskPool> pool,
    TaskPoolSettings settings, Action<string> log) : IPoolStarter
{
    /// <summary>The longest one-line title a pool batch gets; pull request titles are cut shorter anyway.</summary>
    public const int MaxTitle = 72;

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
        _ = Task.Run(() => FinishAsync(id, request), CancellationToken.None);
        return null;
    }

    private async Task FinishAsync(string runId, PoolStartRequest request)
    {
        var ct = CancellationToken.None;
        var keys = request.Items.Select(i => i.Key).ToList();
        try
        {
            var result = runs.Find(runId) is { } handle ? await handle.Done.ConfigureAwait(false) : null;
            var target = pool();
            if (result is not null)
                await target.ReportAsync(request.Worker, result.Usage.Input + result.Usage.Output, ct).ConfigureAwait(false);
            var reason = result is null ? "failed: lost" : result.Status == ResultStatus.Completed ? null : "failed: " + result.Status.ToString().ToLowerInvariant();
            await target.DoneAsync(keys, reason, ct).ConfigureAwait(false);
            await target.ReleaseAsync(request.Worker, null, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log($"pool: worker {request.Worker} did not finish cleanly: {e.GetType().Name}");
        }
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
