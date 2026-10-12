using System.Globalization;

namespace Chargehand.Driven;

/// <summary>What the host that runs the session containers has free right now (ADR 0045, admission). A field the host does not report is null.</summary>
/// <param name="AvailableMb"><c>MemAvailable</c>: what can be handed out without swapping, page cache included.</param>
/// <param name="Load1">The one-minute load average.</param>
/// <param name="Cpus">The processors the probing process sees.</param>
public sealed record HostSnapshot(long? AvailableMb, long? TotalMb, long? SwapTotalMb, long? SwapFreeMb, double? Load1, int? Cpus);

/// <summary>Where the scheduler gets the host's snapshot: the local probe, or the runner over HTTP.</summary>
public interface IHostSource
{
    /// <returns>Null when the source cannot say (an older runner without <c>GET /host</c>).</returns>
    Task<HostSnapshot?> ReadAsync(CancellationToken ct);
}

/// <summary>Reads <c>/proc/meminfo</c> and <c>/proc/loadavg</c>. The parsing takes text so it is testable; a file that does not exist (macOS) leaves its fields null.</summary>
public sealed class HostProbe(string meminfoPath = "/proc/meminfo", string loadavgPath = "/proc/loadavg") : IHostSource
{
    public Task<HostSnapshot?> ReadAsync(CancellationToken ct) => Task.FromResult<HostSnapshot?>(Parse(ReadOrNull(meminfoPath), ReadOrNull(loadavgPath), Environment.ProcessorCount));

    public static HostSnapshot Parse(string? meminfo, string? loadavg, int? cpus)
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
        return new HostSnapshot(Mb("MemAvailable"), Mb("MemTotal"), Mb("SwapTotal"), Mb("SwapFree"), load, cpus);
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
