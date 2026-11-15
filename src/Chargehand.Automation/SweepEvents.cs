using System.Globalization;
using System.Text.Json;
using Chargehand.Modules.Events.Api;

namespace Chargehand.Automation;

/// <summary>The sweep's events (<c>chargehand.sweep.*</c>) as the orchestrator writes them to the event log.</summary>
public static class SweepEvents
{
    private static readonly EventGrant Grant = new("orchestrator", "chargehand/orchestrator", [], ["*"]);

    /// <summary>Appends one event; the log is a record of the sweep, so a refusal or a failed write never stops it.</summary>
    public static async Task EmitAsync(IEventLog events, TimeProvider clock, string sweepId, string type, string suffix, Action<Utf8JsonWriter> data, CancellationToken ct)
    {
        try
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("specversion", "1.0");
                w.WriteString("id", $"{sweepId}-{suffix}");
                w.WriteString("source", Grant.Source);
                w.WriteString("type", type);
                w.WriteString("subject", $"sweep/{sweepId}");
                w.WriteString("time", clock.GetUtcNow().UtcDateTime.ToString(Envelope.Format, CultureInfo.InvariantCulture));
                w.WriteString("scope", "global");
                w.WriteStartObject("data");
                data(w);
                w.WriteEndObject();
                w.WriteEndObject();
            }

            using var doc = JsonDocument.Parse(buffer.ToArray());
            var (stored, _) = Envelope.Check(doc.RootElement, Grant);
            if (stored is not null) await events.AppendAsync([stored], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }
}
