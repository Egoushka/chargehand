using System.Globalization;
using Chargehand.Config;

namespace Chargehand.Driven;

/// <summary>Whether a task may start now, and why not or why only just (ADR 0045, admission).</summary>
/// <param name="Admit">True: start the task.</param>
/// <param name="Detail">When waiting, what is short. When admitting, set only for a task let in against the rule because nothing of this server was running.</param>
public sealed record AdmissionDecision(bool Admit, string? Detail = null);

/// <summary>Lets a task start only when the host has memory for it: available memory minus the reserve must cover the task's request (the working set sessions have measured, see
/// <see cref="MemoryRequest"/>; the container's limit is only a ceiling), counting the containers started in the last <see cref="Settle"/> at their request plus <see cref="StartMarginMb"/>
/// (they have not grown into it yet). Memory pressure (PSI) must be under its limit, and with a load limit set the one-minute load per processor must be under it.
/// A host that cannot say (no <c>/proc</c>, an older runner) admits by counts as before, and the log says so once; a host with no PSI skips the pressure rule. When nothing of this server is
/// running and the host is still short, one task is admitted anyway, so a batch never waits on memory that only other tenants hold. One instance serves every batch of a server.</summary>
/// <param name="log">Told once that the host is unknown.</param>
public sealed class HostAdmission(IHostSource host, DrivenAdmission settings, TimeSpan? interval = null, TimeProvider? time = null, Action<string>? log = null) : IDisposable
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
