using Chargehand.Memory;

namespace Chargehand.RunLog;

/// <summary>Gathers what memory and services did while one run executed, for its <see cref="RunRecord"/>.</summary>
internal sealed class ExtensionsCollector
{
    private readonly List<MemoryReport> _memory = [];
    private readonly List<ServiceReport> _services = [];

    public void Recalled(IEnumerable<SourceRecall> sources) =>
        _memory.AddRange(sources.Select(s => new MemoryReport(s.Source, s.Items.Count, s.SkippedReason, 0, null)));

    public void Retained(IEnumerable<RetainReport> reports)
    {
        foreach (var r in reports)
            if (_memory.FindIndex(m => m.Source == r.Source) is var i and >= 0)
                _memory[i] = _memory[i] with { Retained = r.Claims, RetainSkipped = r.SkippedReason };
    }

    public void Serviced(IEnumerable<ServiceReport> reports) => _services.AddRange(reports);

    /// <summary>A granted server whose worker could not use it, added to that server's report once; nodes run at the same time.
    /// The tools stay as granted: the grant was made, the connection failed, and the issue says which.</summary>
    public void Unavailable(string server, string issue)
    {
        lock (_services)
            if (_services.FindIndex(s => s.Server == server) is var i and >= 0)
            {
                if (!_services[i].Issues.Contains(issue))
                    _services[i] = _services[i] with { Issues = [.. _services[i].Issues, issue] };
            }
            else
                _services.Add(new ServiceReport(server, [], [issue]));
    }

    /// <summary>Null when nothing was recorded, so a run without extensions writes the record it always did.</summary>
    public ExtensionsReport? ToReport()
    {
        lock (_services)
            return _memory.Count + _services.Count == 0 ? null : new ExtensionsReport([.. _memory], [.. _services]);
    }
}
