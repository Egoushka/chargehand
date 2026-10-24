using System.Globalization;
using Chargehand.Contracts;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Automation;

/// <summary>Starts recurring work on a clock, within a monthly cap (ADR 0068). It wakes every minute. An entry is due when its next occurrence after the time it was last dealt with has passed; a slot missed while
/// the server was down is therefore one run at the first wake, not one per slot. An occurrence is dealt with when it starts, when the month's cap is used, or when the entry's last run is still going;
/// a start that fails (the server halted, the checkout will not fetch, the notification service is down) is tried again after <see cref="RetryAfter"/>. Everything it did is in <see cref="ScheduleState"/> before the next step.
/// <paramref name="flakes"/> is the CI history the <c>flaky-tests</c> template is given; without it the template reads the test code only. <paramref name="alerts"/> reads the open alerts of
/// <c>security-alerts</c> as the GitHub App; without it that template starts nothing.</summary>
public sealed class Scheduler(AutomationSchedules settings, IRunLog log, IRepositoryHeads heads, ScheduleState state, IScheduleNotifier notifier, IMorningSource morning, TimeProvider? clock = null,
    Action<string>? warn = null, IFlakeHistory? flakes = null, ISecurityAlerts? alerts = null, ManagedClones? clones = null)
{
    /// <summary>What a start did: a run (or the report) started and counts against the cap; nothing was due to start, yet the occurrence is dealt with; or it is tried again later.</summary>
    private enum Start { Started, Nothing, Later }

    public const string ReadOnlyPreset = "default";
    public const string DrivenPreset = "driven";
    public const int MaxPostAttempts = 30;

    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    /// <summary>A run with no run record and an owner that is gone is lost after this long (a restart gives the resumer time to write the batch's start again).</summary>
    public static readonly TimeSpan LostAfter = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _time = clock ?? TimeProvider.System;
    private readonly Action<string> _warn = warn ?? Warned;
    private readonly CheckoutSources _checkouts = new(heads, clones, "schedules", warn ?? Warned, warn ?? Warned);
    private readonly Dictionary<string, DateTimeOffset> _retryAt = [];

    private static void Warned(string message) => Console.Error.WriteLine($"chargehand: {message}");

    /// <summary>Wakes until <paramref name="ct"/> ends. A failed wake is reported and tried again at the next one.</summary>
    public async Task RunAsync(IBatchStarter starter, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(starter, ct);
            }
            // An HttpClient timeout is a cancellation too: only the caller's own ends the loop.
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _warn($"schedules: wake failed: {e.Message}");
            }
            try
            {
                await Task.Delay(Tick, _time, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One wake: report the runs that ended, then start what is due.</summary>
    public async Task TickAsync(IBatchStarter starter, CancellationToken ct)
    {
        await ReportEndedAsync(ct);
        foreach (var entry in settings.Entries ?? [])
        {
            try
            {
                await DueAsync(entry, starter, ct);
            }
            catch (IntakeException e)
            {
                _warn($"schedules: {entry.Id}: {e.Message}");
                _retryAt[entry.Id] = _time.GetUtcNow() + RetryAfter;
            }
        }
    }

    private async Task DueAsync(ScheduleEntry entry, IBatchStarter starter, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (!state.Handled.TryGetValue(entry.Id, out var last))
        {
            // The first wake of an entry starts at that moment: an occurrence from before it is not owed.
            state.Handled[entry.Id] = now;
            state.Save(now);
            return;
        }
        if (entry.Schedule().Next(last) > now || (_retryAt.TryGetValue(entry.Id, out var retry) && retry > now))
            return;
        if (state.StartsIn(entry.Id, now) >= entry.Cap())
            Consume(entry, now, $"the cap of {entry.Cap().ToString(CultureInfo.InvariantCulture)} starts in {ScheduleState.MonthOf(now)} is used");
        else if (state.Running.Any(r => r.Entry == entry.Id))
            Consume(entry, now, "its last run is still going");
        else if (await StartAsync(entry, starter, now, ct) is var start and not Start.Later)
        {
            state.Handled[entry.Id] = now;
            if (start == Start.Started)
                state.CountStart(entry.Id, now);
            state.Save(now);
        }
    }

    private void Consume(ScheduleEntry entry, DateTimeOffset now, string why)
    {
        _warn($"schedules: {entry.Id}: skipped this occurrence: {why}");
        state.Handled[entry.Id] = now;
        state.Save(now);
    }

    private async Task<Start> StartAsync(ScheduleEntry entry, IBatchStarter starter, DateTimeOffset now, CancellationToken ct)
    {
        if (entry.Kind() == ScheduleKind.Report)
        {
            await PostAsync(MorningReport.Title(now), MorningReport.Format(await morning.ReadAsync(now - MorningReport.Window, ct)), ct);
            return Start.Started;
        }
        IReadOnlyList<SecurityAlert> taking = [];
        if (entry.Template == ScheduleTemplates.SecurityAlerts && (taking = await AlertsToTakeAsync(entry, now, ct)).Count == 0)
            return Start.Nothing;
        var source = await _checkouts.CurrentAsync(null, entry.Repository!.Path, ct);
        var tasks = taking.Count > 0
            ? [.. taking.Select(a => SecurityAlerts.TaskFor(a, entry.Text()!, entry.Size()))]
            : new List<DrivenTask> { new($"schedule-{entry.Id}", Goal: await GoalAsync(entry, ct), Size: entry.Size()) };
        var started = await starter.StartAsync(Request(entry, source, tasks), ct);
        if (started.RunId is not { } run)
        {
            _warn($"schedules: {entry.Id}: the server did not start the run: {started.Refusal}");
            _retryAt[entry.Id] = now + RetryAfter;
            return Start.Later;
        }
        state.Running.Add(new ScheduledRun(entry.Id, run, now, entry.Kind()));
        if (taking.Count > 0)
            state.Taken[entry.Id] = [.. state.Taken.GetValueOrDefault(entry.Id) ?? [], .. taking.Select(a => a.Key)];
        return Start.Started;
    }

    /// <summary>The open alerts not taken before, at most the entry's <c>max_tasks</c>. Forgets the taken alerts GitHub no longer lists open. An alert list GitHub could not be reached for is an
    /// <see cref="IntakeException"/>: the occurrence is tried again later.</summary>
    private async Task<IReadOnlyList<SecurityAlert>> AlertsToTakeAsync(ScheduleEntry entry, DateTimeOffset now, CancellationToken ct)
    {
        if (alerts is null)
        {
            _warn($"schedules: {entry.Id}: security-alerts reads the alerts as the GitHub App, and driven.integrator has none; skipped");
            return [];
        }
        if (await SecurityAlerts.RepositoryOfAsync(entry.Repository!.Path, ct) is not { } repository)
        {
            _warn($"schedules: {entry.Id}: the checkout's origin is not a GitHub repository; skipped");
            return [];
        }
        var read = await alerts.OpenAlertsAsync(repository, ct);
        foreach (var skipped in read.Skipped)
            _warn($"schedules: {entry.Id}: {skipped}");
        var open = read.Alerts.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        // A kind that could not be read keeps what was taken of it: its alerts are unknown, not closed.
        var unread = read.Skipped.Select(s => s.Split(':')[0]).ToHashSet(StringComparer.Ordinal);
        state.Taken[entry.Id] = [.. (state.Taken.GetValueOrDefault(entry.Id) ?? []).Where(k => open.Contains(k) || unread.Any(kind => k.StartsWith($"{kind}-", StringComparison.Ordinal)))];
        state.Save(now);
        var fresh = SecurityAlerts.Fresh(read.Alerts, state.Taken[entry.Id].ToHashSet(StringComparer.Ordinal), entry.Tasks());
        if (fresh.Count == 0)
            _warn($"schedules: {entry.Id}: no open alert that is not already a task ({read.Alerts.Count.ToString(CultureInfo.InvariantCulture)} open)");
        return fresh;
    }

    private static RunRequest Request(ScheduleEntry entry, RepositoryRef repository, List<DrivenTask> tasks) => entry.Kind() == ScheduleKind.Driven
        ? new RunRequest("request/v1", $"Scheduled: {entry.Id}.", new RequestContext(false, DrivenPreset, Repository: repository), Driven: new RequestDriven(tasks, MaxParallel: 1))
        : new RunRequest("request/v1", tasks[0].Goal!, new RequestContext(false, ReadOnlyPreset, Repository: repository));

    /// <summary>The entry's text; for <c>flaky-tests</c> with the CI history, when it can be read.</summary>
    private async Task<string> GoalAsync(ScheduleEntry entry, CancellationToken ct) =>
        entry.Template == ScheduleTemplates.FlakyTests && flakes is not null && await flakes.ReadAsync(entry.Repository!.Path, ct) is { } history
            ? FlakyTestsGoal.Compose(history)
            : entry.Text()!;

    private async Task PostAsync(string title, string body, CancellationToken ct)
    {
        if (settings.NotifyUrl is { } url)
            await notifier.PostAsync(url, title, body, ct);
    }

    /// <summary>Posts the result of each read-only run that ended and frees the entry for its next occurrence. A driven run needs no post: its pull request and questions are in the inbox.</summary>
    private async Task ReportEndedAsync(CancellationToken ct)
    {
        foreach (var run in state.Running.ToList())
        {
            try
            {
                var entry = await log.ReadAsync(run.RunId, ct);
                if (entry.Run is { } ended)
                    await FinishAsync(run, ScheduleResults.Format(ended.Result), ct);
                else if (_time.GetUtcNow() - run.Started > LostAfter && (entry.Start is null || !entry.Start.OwnerAlive()))
                    await FinishAsync(run, ScheduleResults.Lost(run.Entry), ct);
            }
            catch (IntakeException e)
            {
                _warn($"schedules: {run.Entry}: {e.Message}");
                Retry(run);
            }
        }
    }

    private async Task FinishAsync(ScheduledRun run, string text, CancellationToken ct)
    {
        if (run.Kind == ScheduleKind.ReadOnly && (settings.Entries ?? []).Any(e => e.Id == run.Entry))
            await PostAsync(ScheduleResults.Title(run.Entry), text, ct);
        state.Running.Remove(run);
        state.Save(_time.GetUtcNow());
    }

    private void Retry(ScheduledRun run)
    {
        if (run.Attempts + 1 >= MaxPostAttempts)
        {
            _warn($"schedules: {run.Entry}: gave up posting the result of run {run.RunId}");
            state.Running.Remove(run);
        }
        else
            state.Running[state.Running.IndexOf(run)] = run with { Attempts = run.Attempts + 1 };
        state.Save(_time.GetUtcNow());
    }
}
