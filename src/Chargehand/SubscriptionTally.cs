using Chargehand.Contracts;

namespace Chargehand;

/// <summary>The subscription reading a run ends with (ADR 0098): the one stamped on its latest call. The nodes of a split plan run side by side and report from
/// the thread pool, so each notes its last reading here and the later call wins.</summary>
internal sealed class SubscriptionTally
{
    private readonly object _gate = new();
    private (DateTimeOffset At, SubscriptionUsage Usage)? _latest;

    /// <summary>The reading of the latest call noted; null when none carried one.</summary>
    public SubscriptionUsage? Latest
    {
        get { lock (_gate) return _latest?.Usage; }
    }

    public void Note(DateTimeOffset at, SubscriptionUsage usage)
    {
        lock (_gate)
            if (_latest is not { } now || at >= now.At)
                _latest = (at, usage);
    }

    /// <summary>A run tried again on the next pool starts with no reading of the first try's.</summary>
    public void Clear()
    {
        lock (_gate)
            _latest = null;
    }
}
