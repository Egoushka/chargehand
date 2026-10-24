using System.Globalization;
using System.Text.Json.Serialization;

namespace Chargehand.Modules.Runners.Api;

/// <summary>What the host that runs the session containers has free right now (ADR 0045, admission). A field the host does not report is null.</summary>
/// <param name="AvailableMb"><c>MemAvailable</c>: what can be handed out without swapping, page cache included.</param>
/// <param name="Load1">The one-minute load average.</param>
/// <param name="Cpus">The processors the probing process sees.</param>
/// <param name="MemoryPressureAvg10">Linux pressure stall information, <c>/proc/pressure/memory</c>: the percentage of the last ten seconds in which some task was stalled waiting for memory. Null where the host has none (macOS, a kernel without PSI).</param>
/// <param name="State"><c>draining</c> when the host asks for no new session (a laptop on battery or with its lid closed, ADR 0058); null: ready. Left out of the runner's answer while ready, so a server older than this field still reads it.</param>
/// <param name="StateReason">Why it is draining.</param>
/// <param name="Images">The session images the runner will start (<c>RunnerPolicy.Images</c>), by digest; null: a runner older than this field, read as "unknown", so a mismatch is not claimed from silence.</param>
public sealed record HostSnapshot(long? AvailableMb, long? TotalMb, long? SwapTotalMb, long? SwapFreeMb, double? Load1, int? Cpus, double? MemoryPressureAvg10 = null, string? State = null, string? StateReason = null,
    IReadOnlyList<string>? Images = null)
{
    public const string Draining = "draining";

    [JsonIgnore]
    public bool IsDraining => State == Draining;
}

/// <summary>Where the scheduler gets the host's snapshot: the local probe, or the runner over HTTP.</summary>
public interface IHostSource
{
    /// <returns>Null when the source cannot say (an older runner without <c>GET /host</c>).</returns>
    Task<HostSnapshot?> ReadAsync(CancellationToken ct);
}

/// <summary>Reads <c>/proc/meminfo</c>, <c>/proc/loadavg</c> and <c>/proc/pressure/memory</c>. The parsing takes text so it is testable; a file that does not exist (macOS) leaves its fields null.</summary>
public sealed class HostProbe(string meminfoPath = "/proc/meminfo", string loadavgPath = "/proc/loadavg", string pressurePath = "/proc/pressure/memory") : IHostSource
{
    public Task<HostSnapshot?> ReadAsync(CancellationToken ct) =>
        Task.FromResult<HostSnapshot?>(Parse(ReadOrNull(meminfoPath), ReadOrNull(loadavgPath), Environment.ProcessorCount, ReadOrNull(pressurePath)));

    public static HostSnapshot Parse(string? meminfo, string? loadavg, int? cpus, string? memoryPressure = null)
    {
        var memory = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in (meminfo ?? "").Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && parts[0].EndsWith(':') && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kb))
                memory[parts[0][..^1]] = kb;
        }
        long? Mb(string key) => memory.TryGetValue(key, out var kb) ? kb / 1024 : null;
        double? load = null;
        if ((loadavg ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } first
            && double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            load = parsed;
        return new HostSnapshot(Mb("MemAvailable"), Mb("MemTotal"), Mb("SwapTotal"), Mb("SwapFree"), load, cpus, PressureAvg10(memoryPressure));
    }

    /// <summary>The <c>avg10</c> of the <c>some</c> line (<c>some avg10=0.00 avg60=0.00 avg300=0.00 total=0</c>); null when the text has none.</summary>
    private static double? PressureAvg10(string? text)
    {
        foreach (var line in (text ?? "").Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts is ["some", ..] && parts.FirstOrDefault(p => p.StartsWith("avg10=", StringComparison.Ordinal)) is { } avg
                && double.TryParse(avg["avg10=".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                return percent;
        }
        return null;
    }

    private static string? ReadOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
