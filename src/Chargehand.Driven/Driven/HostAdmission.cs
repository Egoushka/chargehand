using System.Globalization;
using Chargehand.Config;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Driven;

/// <summary>Whether a task may start now, and why not or why only just (ADR 0045, admission).</summary>
/// <param name="Admit">True: start the task.</param>
/// <param name="Detail">When waiting, what is short. When admitting, set only for a task let in against the rule because nothing of this server was running.</param>
/// <param name="Runner">The runner the task was placed on (ADR 0058); null with a single host.</param>
/// <param name="Refuse">The task will not be placed (no runner has been usable for too long): <paramref name="Detail"/> is why, and the batch does not start it.</param>
public sealed record AdmissionDecision(bool Admit, string? Detail = null, string? Runner = null, bool Refuse = false);

/// <summary>What <see cref="HostAdmission.Assess"/> found.</summary>
/// <param name="Fits">Memory and load allow the task.</param>
/// <param name="FreeMb">Available memory minus the reserve and the starts of the last minute; null when the host does not report memory.</param>
/// <param name="Detail">When it does not fit, what is short.</param>
/// <param name="Known">The host reported something; false: it admits by count.</param>
public sealed record HostAssessment(bool Fits, long? FreeMb, string? Detail, bool Known);

/// <summary>Where a batch's tasks start: one host (<see cref="HostAdmission"/>) or several runners (<see cref="RunnerPool"/>, ADR 0058).</summary>
public interface IAdmission
{
    /// <summary>How often a waiting task asks again.</summary>
    TimeSpan Interval { get; }

    /// <param name="runner">Set for a task that must stay on its runner (one a restart took up); null: any.</param>
    /// <param name="requestMb">What the host must have free for the task.</param>
    Task<AdmissionDecision> TryAdmitAsync(int requestMb, string? runner, CancellationToken ct);

    /// <summary>A task admitted with <paramref name="runner"/> (the decision's) has ended.</summary>
    void Ended(string? runner);
}

/// <summary>Lets a task start only when the host has memory for it: available memory minus the reserve must cover the task's request (the working set sessions have measured, see
/// <see cref="MemoryRequest"/>; the container's limit is only a ceiling), counting the containers started in the last <see cref="Settle"/> at their request plus <see cref="StartMarginMb"/>
/// (they have not grown into it yet). Memory pressure (PSI) must be under its limit, and with a load limit set the one-minute load per processor must be under it.
/// A host that cannot say (no <c>/proc</c>, an older runner) admits by counts as before, and the log says so once; a host with no PSI skips the pressure rule. When nothing of this server is
/// running and the host is still short, one task is admitted anyway, so a batch never waits on memory that only other tenants hold. One instance serves every batch of a server.</summary>
/// <param name="log">Told once that the host is unknown.</param>
public sealed class HostAdmission(IHostSource host, DrivenAdmission settings, TimeSpan? interval = null, TimeProvider? time = null, Action<string>? log = null) : IAdmission, IDisposable
{
    /// <summary>How long a started container's request still counts against the host's free memory.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(60);

    /// <summary>Added to a just-started container's request: a session grows past its median while it settles.</summary>
    public const int StartMarginMb = 128;

    /// <summary>How often a waiting task looks at the host again.</summary>
    public TimeSpan Interval { get; } = interval ?? TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly List<(DateTimeOffset At, int Mb)> _recent = [];
    private int _running;
    private bool _saidUnknown;

    /// <summary>Decides, and when it admits, counts the task as running until <see cref="Ended"/>.</summary>
    /// <param name="requestMb">What the host must have free for the task.</param>
    public async Task<AdmissionDecision> TryAdmitAsync(int requestMb, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var snapshot = await ReadAsync(ct);
            var now = _time.GetUtcNow();
            _recent.RemoveAll(r => now - r.At > Settle);
            var shortfall = snapshot is null ? null : Shortage(snapshot, requestMb);
            if (shortfall is not null && _running > 0)
                return new AdmissionDecision(false, $"waiting for {shortfall.Value.Kind} ({shortfall.Value.Facts})");
            _running++;
            _recent.Add((now, requestMb + StartMarginMb));
            return new AdmissionDecision(true, shortfall is null ? null : $"admitted with nothing else running, though {shortfall.Value.Kind} is short ({shortfall.Value.Facts})");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>A task admitted by <see cref="TryAdmitAsync"/> has ended.</summary>
    public void Ended() => Interlocked.Decrement(ref _running);

    Task<AdmissionDecision> IAdmission.TryAdmitAsync(int requestMb, string? runner, CancellationToken ct) => TryAdmitAsync(requestMb, ct);

    void IAdmission.Ended(string? runner) => Ended();

    /// <summary>Tasks admitted and not yet ended.</summary>
    public int Running => Volatile.Read(ref _running);

    /// <summary>Whether a task whose request is <paramref name="requestMb"/> fits on a host that has just reported <paramref name="snapshot"/>, without counting it as started: what the runner pool compares (ADR 0058).
    /// A host that cannot say (no snapshot, or one with neither memory nor load) fits and is not <see cref="HostAssessment.Known"/>.</summary>
    public HostAssessment Assess(HostSnapshot? snapshot, int requestMb)
    {
        if (snapshot is null || (snapshot.AvailableMb is null && snapshot.Load1 is null))
            return new HostAssessment(true, null, null, false);
        lock (_recent)
        {
            _recent.RemoveAll(r => _time.GetUtcNow() - r.At > Settle);
            var shortfall = Shortage(snapshot, requestMb);
            long? free = snapshot.AvailableMb is { } available ? available - settings.ReserveMb - _recent.Sum(r => r.Mb) : null;
            return new HostAssessment(shortfall is null, free, shortfall is null ? null : $"waiting for {shortfall.Value.Kind} ({shortfall.Value.Facts})", true);
        }
    }

    /// <summary>Counts a task as started now, for <see cref="Assess"/> and <see cref="Running"/>, until <see cref="Ended()"/>.</summary>
    public void Commit(int requestMb)
    {
        lock (_recent)
            _recent.Add((_time.GetUtcNow(), requestMb + StartMarginMb));
        Interlocked.Increment(ref _running);
    }

    private async Task<HostSnapshot?> ReadAsync(CancellationToken ct)
    {
        string? why = null;
        HostSnapshot? snapshot = null;
        try
        {
            snapshot = await host.ReadAsync(ct);
            if (snapshot is null)
                why = "the runner does not serve /host";
            else if (snapshot.AvailableMb is null && snapshot.Load1 is null)
                why = "the host reports neither memory nor load";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            why = $"the host could not be read ({e.Message})";
        }
        if (why is not null && !_saidUnknown)
        {
            _saidUnknown = true;
            log?.Invoke($"admission: {why}; tasks start by count only");
        }
        return why is null ? snapshot : null;
    }

    private (string Kind, string Facts)? Shortage(HostSnapshot snapshot, int requestMb)
    {
        if (snapshot.AvailableMb is { } available)
        {
            var started = _recent.Sum(r => r.Mb);
            var free = available - settings.ReserveMb - started;
            if (free < requestMb)
                return ("memory", string.Create(CultureInfo.InvariantCulture,
                    $"{available} MiB available, {settings.ReserveMb} reserved{(started > 0 ? $", {started} just started" : "")}, {requestMb} needed"));
        }
        if (snapshot.MemoryPressureAvg10 is { } pressure && pressure > settings.MaxMemoryPressure)
            return ("memory pressure", string.Create(CultureInfo.InvariantCulture, $"{pressure:0.##} % of the last 10 s stalled on memory, limit {settings.MaxMemoryPressure:0.##} %"));
        if (settings.MaxLoadPerCpu is { } limit && snapshot.Load1 is { } load && snapshot.Cpus is > 0 and var cpus && load / cpus > limit)
            return ("load", string.Create(CultureInfo.InvariantCulture, $"{load / cpus:0.##} per processor, limit {limit:0.##}"));
        return null;
    }
}
