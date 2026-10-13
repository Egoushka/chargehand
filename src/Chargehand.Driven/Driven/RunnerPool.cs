using System.Text.Json;
using Chargehand.Config;
using Chargehand.Kernel;

namespace Chargehand.Driven;

/// <summary>A runner as <see cref="RunnerPool.StatusAsync"/> reports it: whether it answered, why not, its last host figures and the sessions this server runs there.</summary>
public sealed record RunnerStatus(RunnerSite Site, bool Reachable, string? Why, HostSnapshot? Snapshot, int Running, long ElapsedMs);

/// <summary>Picks a runner for each task (ADR 0058). A runner is eligible when it answered <c>GET /host</c> within <see cref="ProbeTimeout"/> (an answer is reused for <see cref="HealthTtl"/>; a
/// runner that did not answer is skipped for 15 seconds, then 30, doubling to 2 minutes), is not draining, has a session slot under its <c>max_sessions</c>, and passes the memory and load rule of
/// ADR 0045 against its own host's figures. Of the eligible runners the one with the most free memory, times its weight, wins; equal scores go to the one listed first, and a runner that
/// cannot report memory comes last. When none is eligible the task waits, and when some runner would take it but for its memory, nothing of this server is running anywhere and no runner is
/// eligible, the one with the most free memory takes it anyway, as ADR 0045 admits one task on an idle host. A task that must stay on its runner (one a restart took up) is counted there and never
/// waits: its session may be running, and the task runner waits out a runner that does not answer. When no runner has been usable for <see cref="Patience"/> the pool refuses the task, and the
/// batch does not start it. One instance serves every batch of a server.</summary>
/// <param name="log">Told when a runner stops and starts answering, and once per runner that cannot report memory.</param>
public sealed class RunnerPool : IAdmission, IDisposable
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan HealthTtl = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

    /// <summary>How long no runner may be usable before the pool refuses a task.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromMinutes(15);

    private sealed class State(RunnerSite site, HostAdmission admission)
    {
        public RunnerSite Site { get; } = site;
        public HostAdmission Admission { get; } = admission;
        public HostSnapshot? Snapshot { get; set; }
        public DateTimeOffset? Probed { get; set; }
        public DateTimeOffset DownUntil { get; set; }
        public string? Why { get; set; }
        public int Failures { get; set; }
        public bool NeedsSweep { get; set; }
        public bool SaidUnknown { get; set; }
    }

    private sealed record Probe(bool Healthy, HostSnapshot? Snapshot, string? Why);

    private readonly List<State> _states;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly TimeSpan _probeTimeout;
    private DateTimeOffset? _noneUsableSince;

    /// <param name="interval">How often a waiting task asks again; <see cref="HostAdmission"/>'s default when null.</param>
    /// <param name="probeTimeout"><see cref="ProbeTimeout"/> when null.</param>
    public RunnerPool(IReadOnlyList<RunnerSite> sites, DrivenAdmission admission, TimeSpan? interval = null, TimeProvider? time = null, Action<string>? log = null, TimeSpan? probeTimeout = null)
    {
        _time = time ?? TimeProvider.System;
        _log = log;
        _probeTimeout = probeTimeout ?? ProbeTimeout;
        Interval = interval ?? TimeSpan.FromSeconds(5);
        _states = [.. sites.Select(s => new State(s, new HostAdmission(s.Host ?? new NoHost(), new DrivenAdmission(s.Config.ReserveMb ?? admission.ReserveMb, admission.MaxLoadPerCpu), interval, _time)))];
    }

    public TimeSpan Interval { get; }

    public IReadOnlyList<RunnerSite> Sites => [.. _states.Select(s => s.Site)];

    /// <summary>Cleans a runner of what no batch owns the first time it answers (ADR 0043); set after the sweep at a server start. Until it has run for a runner, nothing is placed there.</summary>
    public Func<RunnerSite, CancellationToken, Task>? Sweep { get; set; }

    /// <summary>The runner did not answer at the sweep at a server start: it is swept before its first task.</summary>
    public void MarkUnswept(string runner)
    {
        if (Find(runner) is { } state)
            state.NeedsSweep = true;
    }

    /// <summary>A call to the runner found it unreachable (a task could not start there): skip it for a while.</summary>
    public void Failed(string runner, string why)
    {
        if (Find(runner) is { } state)
            lock (state)
                Down(state, why);
    }

    /// <summary>Each runner as admission last saw it, asked again only where its answer is older than <see cref="HealthTtl"/> and it is not skipped; never sweeps. For the console.</summary>
    public async Task<IReadOnlyList<RunnerStatus>> StatusAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            List<RunnerStatus> statuses = [];
            foreach (var state in _states)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var probe = await ProbeAsync(state, ct, sweep: false);
                statuses.Add(new RunnerStatus(state.Site, probe.Healthy, probe.Why, probe.Snapshot, state.Admission.Running, clock.ElapsedMilliseconds));
            }
            return statuses;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AdmissionDecision> TryAdmitAsync(int requestMb, string? runner, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (runner is not null)
            {
                // Already on that runner: count it there, and let the task runner deal with a runner that is away.
                if (Find(runner) is { } pinned)
                    pinned.Admission.Commit(requestMb);
                return new AdmissionDecision(true, Runner: runner);
            }

            var now = _time.GetUtcNow();
            List<string> reasons = [];
            List<(State State, HostAssessment Assessment)> candidates = [];
            var usable = 0;
            foreach (var state in _states)
            {
                var probe = await ProbeAsync(state, ct);
                if (!probe.Healthy)
                {
                    reasons.Add($"{state.Site.Name}: {probe.Why}");
                    continue;
                }
                if (probe.Snapshot is { IsDraining: true } draining)
                {
                    reasons.Add($"{state.Site.Name}: draining{(draining.StateReason is null ? "" : $" ({draining.StateReason})")}");
                    continue;
                }
                usable++;
                if (state.Site.Config.MaxSessions is { } max && state.Admission.Running >= max)
                {
                    reasons.Add($"{state.Site.Name}: {state.Admission.Running} of {max} sessions running");
                    continue;
                }
                var assessment = state.Admission.Assess(probe.Snapshot, requestMb);
                if (!assessment.Known && !state.SaidUnknown)
                {
                    state.SaidUnknown = true;
                    _log?.Invoke($"admission: runner '{state.Site.Name}' reports neither memory nor load; tasks are placed on it by count only");
                }
                candidates.Add((state, assessment));
                if (!assessment.Fits)
                    reasons.Add($"{state.Site.Name}: {assessment.Detail}");
            }

            if (usable == 0)
            {
                _noneUsableSince ??= now;
                return now - _noneUsableSince > Patience
                    ? new AdmissionDecision(false, $"no runner has been usable for {(int)Patience.TotalMinutes} minutes ({string.Join("; ", reasons)})", Refuse: true)
                    : new AdmissionDecision(false, $"waiting for a runner ({string.Join("; ", reasons)})");
            }
            _noneUsableSince = null;

            var fits = candidates.Where(c => c.Assessment.Fits).ToList();
            if (fits.Count > 0)
                return Place(fits.Select((c, i) => (c.State, Score: c.Assessment.FreeMb is { } free ? free * c.State.Site.Config.Weight : -1d, Order: i))
                    .OrderByDescending(c => c.Score).ThenBy(c => c.Order).First().State, requestMb, null);
            if (candidates.Count > 0 && _states.Sum(s => s.Admission.Running) == 0)
            {
                var (forced, why) = candidates.OrderByDescending(c => c.Assessment.FreeMb ?? long.MinValue).Select(c => (c.State, c.Assessment.Detail)).First();
                return Place(forced, requestMb, $"admitted on runner '{forced.Site.Name}' with nothing else running, though its host says: {why}");
            }
            return new AdmissionDecision(false, $"waiting for a runner ({string.Join("; ", reasons)})");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Ended(string? runner)
    {
        if (runner is not null && Find(runner) is { } state)
            state.Admission.Ended();
    }

    public void Dispose()
    {
        _gate.Dispose();
        foreach (var state in _states)
            state.Admission.Dispose();
    }

    private static AdmissionDecision Place(State state, int requestMb, string? detail)
    {
        state.Admission.Commit(requestMb);
        return new AdmissionDecision(true, detail, state.Site.Name);
    }

    private State? Find(string name) => _states.FirstOrDefault(s => s.Site.Name == name);

    private async Task<Probe> ProbeAsync(State state, CancellationToken ct, bool sweep = true)
    {
        var now = _time.GetUtcNow();
        if (now < state.DownUntil)
            return new Probe(false, null, $"unreachable ({state.Why}), asked again in {(int)Math.Ceiling((state.DownUntil - now).TotalSeconds)} s");
        if (state.Probed is { } at && now - at < HealthTtl)
            return new Probe(true, state.Snapshot, null);

        HostSnapshot? snapshot = null;
        try
        {
            if (state.Site.Host is { } host)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_probeTimeout);
                try
                {
                    snapshot = await host.ReadAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"no answer in {_probeTimeout.TotalSeconds:0.#} s");
                }
            }
            if (sweep && state.NeedsSweep && Sweep is { } sweeper)
            {
                await sweeper(state.Site, ct);
                state.NeedsSweep = false;
            }
        }
        catch (JsonException)
        {
            // A runner newer than the server answers with a member this server does not know: it is up; it is judged by count.
            snapshot = null;
        }
        catch (Exception e) when (e is ChargehandException or HttpRequestException or TimeoutException or IOException)
        {
            Down(state, e.Message);
            return new Probe(false, null, $"unreachable ({e.Message}), asked again in {(int)Math.Ceiling((state.DownUntil - _time.GetUtcNow()).TotalSeconds)} s");
        }
        if (state.Failures > 0)
            _log?.Invoke($"runner '{state.Site.Name}' answers again");
        (state.Failures, state.Snapshot, state.Probed) = (0, snapshot, now);
        return new Probe(true, snapshot, null);
    }

    private void Down(State state, string why)
    {
        state.Failures++;
        var backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, FirstBackoff.Ticks << Math.Min(state.Failures - 1, 4)));
        (state.DownUntil, state.Why, state.Probed) = (_time.GetUtcNow() + backoff, why, null);
        _log?.Invoke($"runner '{state.Site.Name}' skipped for {backoff.TotalSeconds:0} s: {why}");
    }

    private sealed class NoHost : IHostSource
    {
        public Task<HostSnapshot?> ReadAsync(CancellationToken ct) => Task.FromResult<HostSnapshot?>(null);
    }
}
