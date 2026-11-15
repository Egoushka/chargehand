using System.Globalization;
using System.Text.Json;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Automation;

/// <summary>The sweep now running: its id is the one <c>POST /v1/sweep</c> answered with.</summary>
public sealed record SweepRunning(string Id, string Trigger, DateTimeOffset Started);

/// <summary>The sweep's loop: starts one on the clock and on request, never two at once, and keeps the running sweep and the last report. The profile's settings are the default; the owner's overrides
/// in <see cref="IntakeSources"/> are read at every tick, so a change needs no restart.</summary>
/// <param name="create">Builds a sweep over the repositories that are switched on now.</param>
/// <param name="statePath">Where the time of the last sweep is kept, so a restart can catch up one that was missed by less than <see cref="SweepSchedule.MissWindow"/>; null: kept in memory.</param>
public sealed class SweepHost(SweepSettings profile, IntakeSources sources, IReadOnlyDictionary<string, AutomationRepository> repositories,
    Func<IReadOnlyDictionary<string, AutomationRepository>, IssueSweep> create, TimeProvider clock, string? statePath = null, Action<string>? warn = null) : IDisposable
{
    public static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private SweepRunning? _current;
    private Task _running = Task.CompletedTask;
    private DateTimeOffset? _lastRun;
    private bool _loaded;

    public SweepSettings Profile => profile;

    public IntakeSources Sources => sources;

    public IReadOnlyDictionary<string, AutomationRepository> Repositories => repositories;

    private SweepReport? _last;

    public SweepReport? Last
    {
        get
        {
            lock (_gate)
                return _last;
        }
    }

    public SweepRunning? Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    /// <summary>The profile's settings with the owner's overrides laid over them.</summary>
    public SweepSettings Effective()
    {
        var o = sources.Sweep;
        return profile with { Enabled = o.Enabled ?? profile.Enabled, At = o.At ?? profile.At, Days = o.Days ?? profile.Days };
    }

    /// <returns>The next scheduled time, or null when the schedule is off.</returns>
    public DateTimeOffset? Next(DateTimeOffset now) => Effective() is { Enabled: true } s ? SweepSchedule.NextAfter(s, now) : null;

    /// <summary>Starts a sweep unless one runs; the id is the running one's when it does.</summary>
    public string Start(string trigger)
    {
        lock (_gate)
        {
            if (_current is not null)
                return _current.Id;
            var run = new SweepRunning(IssueSweep.NewId(), trigger, clock.GetUtcNow());
            _current = run;
            _running = Task.Run(() => RunAsync(run), CancellationToken.None);
            Remember(run.Started);
            return run.Id;
        }
    }

    /// <summary>One look at the clock: starts a sweep when a slot is due. A slot that passes while the schedule is off, or on a day it skips, is gone for good.</summary>
    /// <returns>The id of the sweep started; null when none.</returns>
    public string? Tick()
    {
        var now = clock.GetUtcNow();
        var settings = Effective();
        var last = LastRun() ?? now;
        if (settings.Enabled && SweepSchedule.MissedWithin(settings, last, now, SweepSchedule.MissWindow))
            return Current is null ? Start("schedule") : null;
        lock (_gate)
            _lastRun = now;
        return null;
    }

    public async Task LoopAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        while (!linked.IsCancellationRequested)
        {
            try
            {
                Tick();
                await Task.Delay(TickEvery, clock, linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                warn?.Invoke($"sweep: the schedule could not be read: {e.GetType().Name}");
                await Task.Delay(TickEvery, clock, linked.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <summary>Waits for the running sweep (the server's stop).</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await _stop.CancelAsync();
        await _running.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public Task WhenIdleAsync() => _running;

    public void Dispose() => _stop.Dispose();

    private async Task RunAsync(SweepRunning run)
    {
        try
        {
            var on = repositories.Where(r => sources.SweepOn(r.Key)).ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
            var report = await create(on).RunAsync(run.Trigger, _stop.Token, run.Id);
            lock (_gate)
                _last = report;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            warn?.Invoke($"sweep {run.Id} did not finish: {e.GetType().Name}");
        }
        finally
        {
            lock (_gate)
                _current = null;
        }
    }

    private DateTimeOffset? LastRun()
    {
        lock (_gate)
        {
            if (_loaded)
                return _lastRun;
            _loaded = true;
            if (statePath is not null && File.Exists(statePath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
                    _lastRun = DateTimeOffset.Parse(doc.RootElement.GetProperty("last_run").GetString()!, CultureInfo.InvariantCulture);
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or FormatException or IOException or InvalidOperationException)
                {
                    warn?.Invoke("sweep: the file of the last run is unreadable; the schedule starts from now");
                }
            }

            return _lastRun;
        }
    }

    private void Remember(DateTimeOffset at)
    {
        lock (_gate)
        {
            _loaded = true;
            _lastRun = at;
            if (statePath is null)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
                var temp = statePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new Dictionary<string, string> { ["last_run"] = at.ToString("o", CultureInfo.InvariantCulture) }));
                File.Move(temp, statePath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                warn?.Invoke($"sweep: the time of the last run could not be kept: {e.Message}");
            }
        }
    }
}
