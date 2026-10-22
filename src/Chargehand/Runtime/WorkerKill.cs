using System.Globalization;

namespace Chargehand.Runtime;

/// <summary>
/// A worker process that died of SIGKILL (exit 137) without chargehand interrupting it: on a server in a container that is
/// almost always the kernel's out-of-memory killer. The runtime words the error with <see cref="Describe"/>, the node
/// recognises it with <see cref="Is"/> and advises on memory instead of on a model or credential.
/// </summary>
public static class WorkerKill
{
    public const int ExitCode = 137;
    private const string Marker = "was killed (exit 137, SIGKILL)";

    public static string Describe(string? memory) =>
        $"claude {Marker} without chargehand stopping it, most often by the out-of-memory killer" + (memory is null ? "" : $"; {memory}");

    public static bool Is(string? error) => error?.Contains(Marker, StringComparison.Ordinal) == true;

    /// <summary>What the cgroup (v2) at <paramref name="root"/> says about memory: peak and limit in MB and the out-of-memory
    /// kills so far, e.g. <c>container memory peak 2335 MB of 2560 MB, 1 out-of-memory kill</c>; null when it says nothing.</summary>
    public static string? Memory(string root = "/sys/fs/cgroup")
    {
        var peak = Mb(Read(root, "memory.peak"));
        var max = Mb(Read(root, "memory.max"));
        var kills = Read(root, "memory.events")?.Split('\n').Select(l => l.Split(' ')).FirstOrDefault(f => f.Length == 2 && f[0] == "oom_kill") is { } line
            && long.TryParse(line[1], CultureInfo.InvariantCulture, out var n) ? n : 0;
        var parts = new List<string>();
        if (peak is not null)
            parts.Add($"container memory peak {peak} MB" + (max is null ? "" : $" of {max} MB"));
        if (kills > 0)
            parts.Add($"{kills} out-of-memory kill{(kills == 1 ? "" : "s")}");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string? Read(string root, string file)
    {
        try
        {
            return File.ReadAllText(Path.Combine(root, file)).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Bytes to MB, rounded up; null for a missing file or <c>max</c> (no limit).</summary>
    private static long? Mb(string? bytes) =>
        long.TryParse(bytes, CultureInfo.InvariantCulture, out var b) ? (b + (1 << 20) - 1) >> 20 : null;
}
