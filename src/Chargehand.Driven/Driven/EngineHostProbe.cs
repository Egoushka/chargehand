using Chargehand.Containers;

namespace Chargehand.Driven;

/// <summary>The memory and load of the machine the container engine runs on, asked of the engine (ADR 0058). On Linux the runner reads its own <c>/proc</c>; on a Mac the engine is in a virtual machine with its
/// own memory limit (OrbStack's <c>memory_mib</c>), which is what a session can use and what the Mac's free memory says nothing about. A throwaway container of the session image reads the figures
/// (<see cref="ContainerTemplate.HostProbeArgs"/>), so the image must be on the engine already: a pull in the middle of a probe is slow. The answer is kept for <paramref name="keepFor"/> (5 seconds), so a
/// server that asks often starts one container, and a probe that fails (the engine is away) throws, which the runner answers as an unreachable host.</summary>
public sealed class EngineHostProbe(DockerCliEngine engine, string image, TimeSpan? keepFor = null, TimeProvider? time = null) : IHostSource
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private DateTimeOffset _at = DateTimeOffset.MinValue;
    private HostSnapshot? _last;

    public async Task<HostSnapshot?> ReadAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
            if (_last is not null && now - _at < (keepFor ?? TimeSpan.FromSeconds(5)))
                return _last;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        string text;
        try
        {
            text = await engine.ProbeHostAsync(image, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new Chargehand.Runner.RunnerUnreachableException("the engine did not answer the host probe in 20 seconds (is the session image on it?)");
        }
        var snapshot = Parse(text);
        lock (_gate)
            (_last, _at) = (snapshot, now);
        return snapshot;
    }

    /// <summary>The probe container's output: <c>/proc/meminfo</c>, then the load, the memory pressure (when the kernel has it) and <c>nproc</c>, each after its marker.</summary>
    public static HostSnapshot Parse(string text)
    {
        var (memory, rest) = Split(text, ContainerTemplate.ProbeLoadMarker);
        var (load, rest2) = Split(rest, ContainerTemplate.ProbePressureMarker);
        var (pressure, cpus) = Split(rest2, ContainerTemplate.ProbeCpusMarker);
        return HostProbe.Parse(memory, load, int.TryParse(cpus.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null, pressure);
    }

    private static (string Before, string After) Split(string text, string marker)
    {
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? (text, "") : (text[..at], text[(at + marker.Length)..]);
    }
}
