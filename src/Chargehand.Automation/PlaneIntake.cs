using System.Globalization;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;

namespace Chargehand.Automation;

/// <summary>Starts driven batches from Plane (ADR 0067), the sibling of <see cref="GitHubIntake"/>. Each poll reads the work items of every listed project that are in the trigger state; one that was not
/// started for its current update time starts a batch of one task whose goal is the item, and is moved to the started state with a comment. When the batch ends the item gets a comment with the pull
/// request (or why there is none) and moves to the review state (or the failed state). What it acted on is recorded in <see cref="PlaneState"/> before the comment about it, so a restart cannot start
/// an item twice. The states are found by name at each poll, so renaming one in Plane takes effect, and a project whose states are not all there is skipped with a line on stderr. A project switched off in
/// <see cref="Sources"/> (CHARGEHAND-242) is not read; its batches still report.</summary>
public sealed class PlaneIntake(AutomationPlane settings, IPlaneHost host, IRunLog log, IRepositoryHeads heads, PlaneState state, TimeProvider? clock = null, Action<string>? warn = null,
    IntakeSources? sources = null, ManagedClones? clones = null)
{
    private const int MaxDetail = 600;
    private const int MaxQuestions = 10;
    private const int MaxQuestion = 500;

    private readonly TimeProvider _time = clock ?? TimeProvider.System;
    private readonly Action<string> _warn = warn ?? Warned;
    private readonly CheckoutSources _checkouts = new(heads, clones, "plane intake", warn ?? Warned, warn ?? Warned);
    private readonly Dictionary<string, string> _identifiers = [];

    private static void Warned(string message) => Console.Error.WriteLine($"chargehand: {message}");

    private enum Outcome { Started, Settled, Deferred }

    /// <summary>The on/off switch of each listed project, by project id.</summary>
    public IntakeSources Sources { get; } = sources ?? new IntakeSources();

    /// <summary>The listed projects, by Plane project id.</summary>
    public IReadOnlyDictionary<string, PlaneProject> Projects => settings.Projects ?? new Dictionary<string, PlaneProject>();

    private sealed record Names(string Trigger, string Started, string Review, string? Failed);

    /// <summary>Polls until <paramref name="ct"/> ends. A failed poll is reported and tried again at the next one.</summary>
    public async Task RunAsync(IBatchStarter starter, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollAsync(starter, ct);
            }
            // An HttpClient timeout is a cancellation too: only the caller's own ends the loop.
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _warn($"plane intake: poll failed: {e.Message}");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.PollSeconds), _time, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One pass: report the batches that ended, then read each listed project. A project Plane will not answer for is skipped until the next pass.</summary>
    public async Task PollAsync(IBatchStarter starter, CancellationToken ct)
    {
        await ReportEndedAsync(ct);
        foreach (var (project, checkout) in Projects)
        {
            if (!Sources.IsOn(project))
                continue;
            try
            {
                await ProjectAsync(project, checkout, starter, ct);
            }
            catch (IntakeException e)
            {
                _warn($"plane intake: {project}: {e.Message}");
            }
        }
    }

    private Names Configured => new(settings.TriggerState!, settings.StartedState!, settings.ReviewState!, settings.FailedState);

    private async Task ProjectAsync(string project, PlaneProject checkout, IBatchStarter starter, CancellationToken ct)
    {
        var states = await host.StatesAsync(project, ct);
        var names = Configured;
        string? Id(string? name) => name is null ? null : states.FirstOrDefault(s => string.Equals(s.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;
        if (Id(names.Trigger) is not { } trigger || Id(names.Started) is not { } started || Id(names.Review) is null)
        {
            _warn($"plane intake: {project}: the project has no state named '{names.Trigger}', '{names.Started}' or '{names.Review}'");
            return;
        }
        foreach (var item in (await host.ItemsInStateAsync(project, trigger, ct)).OrderBy(i => i.UpdatedAt))
        {
            var key = KeyOf(item);
            if (state.Handled.Contains(key) || state.Tasks.Any(t => t.Item == item.Id && t.Phase == PlanePhase.Running))
                continue;
            try
            {
                await StartAsync(key, project, checkout, item, started, starter, ct);
            }
            catch (IntakeException e)
            {
                _warn($"plane intake: {project}: {item.Id}: {e.Message}");
            }
        }
    }

    private static string KeyOf(PlaneItem item) => $"{item.Id}@{item.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}";

    private async Task<string> IdentifierAsync(string project, PlaneItem item, CancellationToken ct)
    {
        if (!_identifiers.TryGetValue(project, out var prefix))
            _identifiers[project] = prefix = await host.ProjectIdentifierAsync(project, ct);
        return $"{prefix}-{item.SequenceId.ToString(CultureInfo.InvariantCulture)}";
    }

    private async Task<Outcome> StartAsync(string key, string project, PlaneProject checkout, PlaneItem listed, string startedState, IBatchStarter starter, CancellationToken ct)
    {
        var identifier = await IdentifierAsync(project, listed, ct);
        if (await CapsAsync(key, project, listed.Id, ct) is { } held)
            return held;
        RepositoryRef source;
        try
        {
            source = await _checkouts.CurrentAsync(null, checkout.Path, ct);
        }
        catch (IntakeException e)
        {
            _warn($"plane intake: {identifier}: {e.Message}");
            return Outcome.Deferred;
        }
        // The list may leave the description out or cut it; the item itself is the source.
        var item = await host.GetAsync(project, listed.Id, ct);
        var taskId = PlaneGoal.TaskId(identifier);
        var task = new DrivenTask(taskId, Goal: PlaneGoal.Build(identifier, item.Name, item.Description), Size: settings.BudgetClass);
        var request = new RunRequest("request/v1", $"Triggered from Plane: {identifier}.", new RequestContext(false, GitHubIntake.Preset, Repository: source),
            Driven: new RequestDriven([task], MaxParallel: 1, MaxTokensTotal: settings.MaxTokensTotal, MaxUsdTotal: settings.MaxUsdTotal));
        var started = await starter.StartAsync(request, ct);
        if (started.RunId is not { } run)
        {
            _warn($"plane intake: {identifier}: the server did not start the batch: {started.Refusal}");
            return Outcome.Deferred;
        }
        var now = _time.GetUtcNow();
        state.Tasks.Add(new PlaneTask(project, listed.Id, identifier, run, taskId, now));
        state.Starts.Add(now);
        state.Handled.Add(key);
        state.Save(now);
        await TryAsync(identifier, "the state change", () => host.SetStateAsync(project, listed.Id, startedState, ct));
        await TryAsync(identifier, "the comment", () => host.CommentAsync(project, listed.Id,
            $"chargehand started run {run} (task {taskId}){(checkout.Remote is null ? "" : $" on {checkout.Remote}")}. It works on its own branch and opens a draft pull request when it is done; the link comes here.", ct));
        return Outcome.Started;
    }

    /// <summary>Waits or refuses where a cap says so: null when the batch may start.</summary>
    private async Task<Outcome?> CapsAsync(string key, string project, string item, CancellationToken ct)
    {
        if (state.Tasks.Count(t => t.Phase == PlanePhase.Running) >= settings.MaxConcurrent)
        {
            await NotifyOnceAsync($"wait:{key}", project, item, $"chargehand is waiting for a free slot: {settings.MaxConcurrent.ToString(CultureInfo.InvariantCulture)} triggered batches are running. This starts when one ends.", ct);
            return Outcome.Deferred;
        }
        if (state.Starts.Count(s => s > _time.GetUtcNow() - TimeSpan.FromDays(1)) >= settings.MaxStartsPerDay)
        {
            await NotifyOnceAsync($"cap:{key}", project, item,
                $"chargehand did not start: {settings.MaxStartsPerDay.ToString(CultureInfo.InvariantCulture)} triggered batches have started in the last 24 hours, which is the limit. Move the item to {settings.TriggerState} again later.", ct);
            state.Handled.Add(key);
            state.Save(_time.GetUtcNow());
            return Outcome.Settled;
        }
        return null;
    }

    /// <summary>Says how the batches that ended ended, and frees their slots.</summary>
    private async Task ReportEndedAsync(CancellationToken ct)
    {
        foreach (var task in state.Tasks.Where(t => t.Phase == PlanePhase.Running).ToList())
        {
            try
            {
                var entry = await log.ReadAsync(task.BatchRunId, ct);
                if (entry.Run is { } run)
                    await FinishAsync(task, run.Result, ct);
                else if (_time.GetUtcNow() - task.Started > GitHubIntake.LostAfter && (entry.Start is null || !entry.Start.OwnerAlive()))
                    await FinishAsync(task, null, ct);
            }
            catch (IntakeException e)
            {
                _warn($"plane intake: {task.Identifier}: {e.Message}");
            }
        }
    }

    private async Task FinishAsync(PlaneTask task, ResultContract? result, CancellationToken ct)
    {
        var row = result is null ? null : PlaneOutcome.Of(result, task.TaskId);
        var finished = row is { Status: "completed" };
        var text = await DescribeAsync(task, result, row, ct);
        try
        {
            await host.CommentAsync(task.Project, task.Item, text, ct);
        }
        catch (IntakeException e) when (e.Status is 404 or 410)
        {
            // The item is gone: there is nobody to tell, and nothing to move.
            state.Replace(task, task with { Phase = PlanePhase.Done });
            state.Save(_time.GetUtcNow());
            return;
        }
        state.Replace(task, task with { Phase = PlanePhase.Done });
        state.Save(_time.GetUtcNow());
        await MoveAsync(task, finished, ct);
    }

    private async Task MoveAsync(PlaneTask task, bool finished, CancellationToken ct)
    {
        var target = finished ? settings.ReviewState : settings.FailedState;
        if (target is null)
            return;
        try
        {
            var id = (await host.StatesAsync(task.Project, ct)).FirstOrDefault(s => string.Equals(s.Name.Trim(), target.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;
            if (id is null)
                _warn($"plane intake: {task.Identifier}: the project has no state named '{target}'");
            else
                await host.SetStateAsync(task.Project, task.Item, id, ct);
        }
        catch (IntakeException e)
        {
            _warn($"plane intake: {task.Identifier}: the state change was not made: {e.Message}");
        }
    }

    private async Task<string> DescribeAsync(PlaneTask task, ResultContract? result, PlaneOutcome? row, CancellationToken ct)
    {
        if (result is null)
            return $"chargehand lost run {task.BatchRunId}: the server that ran it stopped and did not take it up again. Move the item to {settings.TriggerState} again to start the task again.";
        if (row is null)
            return $"chargehand's run {task.BatchRunId} ended without starting the task: {PlaneGoal.Cut(result.Error?.Message ?? result.Summary, MaxDetail)}";
        if (row.Status == "completed")
            return $"chargehand finished run {task.BatchRunId}: draft pull request {row.PrUrl}";
        if (row.Status == "needs_input")
        {
            var questions = row.RunId is null ? [] : (await log.ReadAsync(row.RunId, ct)).Run?.Result.OpenQuestions ?? [];
            return $"chargehand needs an answer before it can go on (run {task.BatchRunId}):\n\n"
                + string.Join('\n', (questions.Count == 0 ? [row.Detail ?? "the session did not say what it needs"] : questions).Take(MaxQuestions).Select(q => $"- {PlaneGoal.Cut(q, MaxQuestion)}"))
                + $"\n\nPlane intake does not read replies. Put the answer in the description and move the item to {settings.TriggerState} again.";
        }
        return $"chargehand's run {task.BatchRunId} ended without a finished pull request ({row.Status.Replace('_', ' ')}): {PlaneGoal.Cut(row.Detail ?? "no detail", MaxDetail)}"
            + (row.PrUrl is null ? "" : $"\n\nWhat was done so far: {row.PrUrl}");
    }

    /// <summary>A note that is written once per key. The key is kept only after the comment is up, so a comment Plane refused is tried again.</summary>
    private async Task NotifyOnceAsync(string key, string project, string item, string text, CancellationToken ct)
    {
        if (state.Notified.Contains(key))
            return;
        if (await TryAsync(item, "the comment", () => host.CommentAsync(project, item, text, ct)))
        {
            state.Notified.Add(key);
            state.Save(_time.GetUtcNow());
        }
    }

    private async Task<bool> TryAsync(string item, string what, Func<Task> call)
    {
        try
        {
            await call();
            return true;
        }
        catch (IntakeException e)
        {
            _warn($"plane intake: {item}: {what} was not made: {e.Message}");
            return false;
        }
    }
}
