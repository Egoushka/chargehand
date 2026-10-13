using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Driven;
using Microsoft.AspNetCore.Http;

namespace Chargehand.Server;

/// <summary>A driven batch's events from the journal (ADR 0061), each with its sequence number as the SSE id. A client that reconnects sends the last one as <c>Last-Event-ID</c> and gets
/// every event after it, in order and once, also from the process that took the batch up after a restart. A task's progress comes as the records keep it, at most once in 30 seconds.</summary>
internal static class BatchEventStream
{
    /// <summary>The sequence number a reconnecting client saw last; 0 (from the first event) when it sends none.</summary>
    public static long After(HttpRequest request) =>
        long.TryParse(request.Headers["Last-Event-ID"], NumberStyles.None, CultureInfo.InvariantCulture, out var after) ? after : 0;

    /// <summary>The journaled events after <paramref name="after"/>, then each new one, until the run finishes. Its <c>run_finished</c> is not journaled (the batch's flow is forgotten just
    /// before it), so it comes from the run itself, without an id.</summary>
    public static async IAsyncEnumerable<SseItem<string>> Of(RunHandle run, Func<long, CancellationToken, Task<IReadOnlyList<(long Sequence, RunStatus Event)>>> read, long after,
        [EnumeratorCancellation] CancellationToken ct)
    {
        for (var cursor = after; ;)
        {
            // Taken before the read: an event is journaled before it is published, so one published after the read wakes the loop.
            var changed = run.Changed;
            var events = await read(cursor, ct);
            foreach (var (sequence, e) in events)
            {
                cursor = sequence;
                yield return Item(e, sequence);
            }
            if (events.Count > 0)
                continue;
            if (run.Latest is { Event: RunEventKind.RunFinished } last)
            {
                yield return Item(last, null);
                yield break;
            }
            await changed.WaitAsync(ct);
        }
    }

    private static SseItem<string> Item(RunStatus e, long? sequence) =>
        new(JsonSerializer.Serialize(e, ContractJsonContext.Default.RunStatus), BatchRecords.KindOf(e)) { EventId = sequence?.ToString(CultureInfo.InvariantCulture) };
}
