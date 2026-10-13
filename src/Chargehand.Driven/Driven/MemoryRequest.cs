using Chargehand.Config;

namespace Chargehand.Driven;

/// <summary>Where a task's admission request came from: <c>history</c> (the <paramref name="Percentile"/>th percentile of <paramref name="N"/> recorded peaks for <paramref name="Scope"/>,
/// <c>repository</c> or <c>all</c>) or <c>default</c>.</summary>
public sealed record MemoryRequestChoice(int Mb, string Source, int? N = null, int? Percentile = null, string? Scope = null)
{
    public string Describe() => Source == "history"
        ? $"{Mb} MiB, the {Percentile}th percentile of {N} recorded session peaks ({Scope})"
        : $"{Mb} MiB, the default (too few recorded session peaks)";
}

/// <summary>What the host must have free for a task (ADR 0045, admission): the working set that sessions have measured, not the container's limit. In this order: the <see cref="Percentile"/>th
/// percentile of the peak memory of the tasks of the last <see cref="WindowDays"/> days for the repository, when there are at least <see cref="MinPeaks"/> of them; the same over all repositories, when
/// the repository alone has too few; else <c>default_request_mb</c>. The result is never below <see cref="DrivenAdmission.MinRequestMb"/> and never above the container's limit. Pure: the records and the
/// time are passed in, like <see cref="BudgetHistory"/>. Every task with a recorded peak counts, whatever its outcome: one the kernel killed for memory peaked at its limit, and that is what a request
/// should learn from.</summary>
public static class MemoryRequest
{
    public const int Percentile = 90;

    /// <summary>Below this many peaks the 90th percentile is just the largest of a handful.</summary>
    public const int MinPeaks = 10;

    public const int WindowDays = 60;

    /// <param name="limitMb">The container's memory limit.</param>
    /// <param name="repository">As <see cref="LearningRecord.Repository"/> names it.</param>
    public static MemoryRequestChoice Choose(DrivenAdmission settings, int limitMb, string repository, IReadOnlyList<LearningRecord> records, DateTimeOffset now)
    {
        var since = now.AddDays(-WindowDays);
        var peaks = records.Where(r => r.MemoryPeakMb is > 0 && r.At >= since).ToList();
        var here = peaks.Where(r => r.Repository == repository).ToList();
        var (scope, used) = here.Count >= MinPeaks ? ("repository", here) : ("all", peaks);
        if (used.Count >= MinPeaks && LearningStats.Percentile(used.Select(r => (decimal)r.MemoryPeakMb!.Value), Percentile) is { } peak)
            return new MemoryRequestChoice(Clamp((int)Math.Ceiling(peak), limitMb), "history", used.Count, Percentile, scope);
        return new MemoryRequestChoice(Clamp(settings.DefaultRequestMb, limitMb), "default");
    }

    private static int Clamp(int mb, int limitMb) => Math.Min(limitMb, Math.Max(DrivenAdmission.MinRequestMb, mb));
}
