using System.Text.Json;
using Chargehand.Modules.Events.Api;

namespace Chargehand.Modules.Warmer.Api;

/// <summary>Tells the event log that a checkout now exists (<see cref="CacheWarmer.CreatedType"/>), by its name under the checkouts root, so the warmer can restore it before an agent starts.</summary>
public static class CheckoutAnnouncer
{
    public const string Source = "chargehand/orchestrator";

    private static readonly EventGrant Grant = new("orchestrator", Source, [], ["*"]);

    /// <summary>Best effort: a log that cannot take the event never stops a run.</summary>
    public static Task AnnounceAsync(IEventLog log, string checkout, CancellationToken ct) => AnnounceAsync(log, checkout, CacheWarmer.CreatedType, ct);

    /// <summary>Same, for a checkout a queued branch reuses (<see cref="CacheWarmer.QueuedType"/>), so the warmer refreshes it before its worker.</summary>
    public static Task AnnounceQueuedAsync(IEventLog log, string checkout, CancellationToken ct) => AnnounceAsync(log, checkout, CacheWarmer.QueuedType, ct);

    private static async Task AnnounceAsync(IEventLog log, string checkout, string type, CancellationToken ct)
    {
        try
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("specversion", "1.0");
                w.WriteString("id", $"wt-{Guid.NewGuid():N}");
                w.WriteString("source", Source);
                w.WriteString("type", type);
                w.WriteString("time", DateTimeOffset.UtcNow.ToString(Envelope.Format, System.Globalization.CultureInfo.InvariantCulture));
                w.WriteString("scope", "global");
                w.WriteStartObject("data");
                w.WriteString("checkout", checkout);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            using var doc = JsonDocument.Parse(buffer.ToArray());
            var (stored, _) = Envelope.Check(doc.RootElement, Grant);
            if (stored is not null)
                await log.AppendAsync([stored], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The warmer is an optimisation; the checkout is already usable.
        }
    }
}
