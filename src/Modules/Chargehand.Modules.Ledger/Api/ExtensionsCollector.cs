namespace Chargehand.Modules.Ledger.Api;

/// <summary>Gathers what memory and services did while one run executed, for its <see cref="RunRecord"/>.</summary>
public sealed class ExtensionsCollector
{
    private readonly List<MemoryReport> _memory = [];
    private readonly List<ServiceReport> _services = [];

    /// <summary>What each memory source recalled: <see cref="MemoryReport.Source"/>, <see cref="MemoryReport.Recalled"/> and <see cref="MemoryReport.RecallSkipped"/> are read.</summary>
    public void Recalled(IEnumerable<MemoryReport> sources) => _memory.AddRange(sources);

    /// <summary>What each memory source retained, added to the report of the source that recalled: <see cref="MemoryReport.Source"/>, <see cref="MemoryReport.Retained"/> and
    /// <see cref="MemoryReport.RetainSkipped"/> are read.</summary>
    public void Retained(IEnumerable<MemoryReport> reports)
    {
        foreach (var r in reports)
            if (_memory.FindIndex(m => m.Source == r.Source) is var i and >= 0)
                _memory[i] = _memory[i] with { Retained = r.Retained, RetainSkipped = r.RetainSkipped };
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
