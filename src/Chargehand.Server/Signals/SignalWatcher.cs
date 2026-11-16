using System.Text;
using System.Text.Json;
using Chargehand.Modules.Events.Api;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Server;

/// <summary>Checks each finished thread of the event log against the owner's signals (P06): a thread is finished when a step closes it, whether chargehand's own work or an interactive session the
/// forwarder reports. Reads only events that arrive from now on. The checker's daily cap bounds the model spend; a hit is kept in <paramref name="hits"/>.</summary>
public sealed class SignalWatcher(IEventLog log, SignalChecker checker, SignalHitStore hits)
{
    public const int DigestEvents = 200;

    public async Task RunAsync(CancellationToken ct)
    {
        var cursor = log.Signal.Latest;
        while (true)
        {
            await log.Signal.WaitAsync(cursor, ct).ConfigureAwait(false);
            foreach (var e in await log.ReadAsync(new EventFilter(After: cursor, Limit: 200), ct).ConfigureAwait(false))
            {
                cursor = e.Seq;
                try
                {
                    await HandleAsync(e, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"signals: event {e.Seq}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>The hits of a thread's closing step; none for any other event.</summary>
    public async Task<IReadOnlyList<SignalHit>> HandleAsync(StoredEvent e, CancellationToken ct)
    {
        if (ThreadStep.Read(e) is not { Closed: true } step)
            return [];
        var events = await log.ReadAsync(new EventFilter(Thread: step.Thread, Limit: DigestEvents), ct).ConfigureAwait(false);
        var found = await checker.CheckAsync(step.Thread, Digest(events), ct).ConfigureAwait(false);
        foreach (var hit in found)
            hits.Append(hit);
        return found;
    }

    /// <summary>One line per event: time, type, and its data cut short. Text from a session, so the judge's prompt calls it data.</summary>
    public static string Digest(IEnumerable<StoredEvent> events)
    {
        var text = new StringBuilder();
        foreach (var e in events)
        {
            using var doc = JsonDocument.Parse(e.Envelope);
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d.GetRawText() : "";
            text.Append(e.Time).Append(' ').Append(e.Type).Append(' ').Append(data.Length > 300 ? data[..300] + "…" : data).Append('\n');
        }
        return text.ToString();
    }
}
