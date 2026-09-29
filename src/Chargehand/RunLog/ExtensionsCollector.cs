using Chargehand.Memory;

namespace Chargehand.RunLog;

/// <summary>Gathers what memory did while one run executed, for its <see cref="RunRecord"/>.</summary>
internal sealed class ExtensionsCollector
{
    private readonly List<MemoryReport> _memory = [];

    public void Recalled(IEnumerable<SourceRecall> sources) =>
        _memory.AddRange(sources.Select(s => new MemoryReport(s.Source, s.Items.Count, s.SkippedReason, 0, null)));

    public void Retained(IEnumerable<RetainReport> reports)
    {
        foreach (var r in reports)
            if (_memory.FindIndex(m => m.Source == r.Source) is var i and >= 0)
                _memory[i] = _memory[i] with { Retained = r.Claims, RetainSkipped = r.SkippedReason };
    }

    /// <summary>Null when nothing was recorded, so a run without extensions writes the record it always did.</summary>
    public ExtensionsReport? ToReport() => _memory.Count == 0 ? null : new ExtensionsReport([.. _memory], []);
}
