using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Chargehand.Modules.Events.Api;

/// <summary>An answer of the event routes: the HTTP status and its JSON body.</summary>
public sealed record EventAnswer(int Status, string Json);

/// <summary>The event log behind <c>/v1/events</c> and <c>/v1/threads</c> (ADR 0090; threads addendum of 2026-11-08): ingest with the envelope's refusals, query and stream
/// filtered by the caller's grants, and the threads and cards derived from <c>thread.step.recorded</c>.</summary>
public sealed class EventService(IEventLog log, IReadOnlyList<(EventGrant Grant, string Token)>? tokens = null) : IDisposable
{
    /// <summary>The most envelopes one <c>POST</c> takes; one transaction and one flush.</summary>
    public const int MaxBatch = 500;

    private readonly IReadOnlyList<(EventGrant Grant, byte[] Token)> tokens = [.. (tokens ?? []).Select(t => (t.Grant, Encoding.UTF8.GetBytes(t.Token)))];
    private readonly SemaphoreSlim appending = new(1, 1);
    private long refused;

    public IEventLog Log { get; } = log;

    /// <summary>How many envelopes were refused since start (the spec counts refusals).</summary>
    public long Refused => Interlocked.Read(ref refused);

    public void Dispose()
    {
        appending.Dispose();
        (Log as IDisposable)?.Dispose();
    }

    /// <summary>The grant of a producer's bearer token, or null.</summary>
    public EventGrant? Grant(string bearer)
    {
        ArgumentNullException.ThrowIfNull(bearer);
        var given = Encoding.UTF8.GetBytes(bearer);
        EventGrant? found = null;
        foreach (var (grant, token) in tokens)
        {
            if (CryptographicOperations.FixedTimeEquals(given, token))
                found = grant;
        }
        return found;
    }

    /// <summary><c>POST /v1/events</c>: one envelope or an array of up to <see cref="MaxBatch"/>, all stored or none. 200 with <c>{"seq":[…]}</c>.</summary>
    public async Task<EventAnswer> AppendAsync(EventGrant grant, JsonElement body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var items = body.ValueKind == JsonValueKind.Array ? [.. body.EnumerateArray()] : new List<JsonElement> { body };
        if (items.Count is 0 or > MaxBatch)
            return Refuse(new EventRefusal(400, "bad_batch", $"send one envelope or an array of 1 to {MaxBatch}"));
        var batch = new List<NewEvent>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var (e, refusal) = Envelope.Check(items[i], grant);
            if (refusal is not null)
                return Refuse(refusal with { Index = i });
            batch.Add(e!);
        }
        await appending.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await FirstStepWithoutGoal(items, batch, ct).ConfigureAwait(false) is { } index)
                return Refuse(new EventRefusal(400, "thread_step_invalid", "a thread's first step gives 'data.goal'", index));
            var seqs = await Log.AppendAsync(batch, ct).ConfigureAwait(false);
            return new EventAnswer(200, Json(w =>
            {
                w.WriteStartArray("seq");
                foreach (var seq in seqs)
                    w.WriteNumberValue(seq);
                w.WriteEndArray();
            }));
        }
        finally
        {
            appending.Release();
        }
    }

    /// <summary><c>GET /v1/events</c>: oldest first after <see cref="EventQuery.After"/>.</summary>
    public async Task<EventAnswer> QueryAsync(EventGrant grant, EventQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Problem(grant) is { } problem)
            return Refuse(problem, counted: false);
        var found = await Log.ReadAsync(query.Filter(grant), ct).ConfigureAwait(false);
        return new EventAnswer(200, Json(w =>
        {
            w.WriteStartArray("events");
            foreach (var e in found)
                w.WriteRawValue(e.Json);
            w.WriteEndArray();
            w.WriteNumber("last_seq", found.Count > 0 ? found[^1].Seq : query.After);
        }));
    }

    /// <summary>The stream's events after <see cref="EventQuery.After"/>, then each new one as it is committed, until <paramref name="ct"/> ends it. Check
    /// <see cref="EventQuery.Problem"/> first.</summary>
    public async IAsyncEnumerable<StoredEvent> StreamAsync(EventGrant grant, EventQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var cursor = query.After;
        while (!ct.IsCancellationRequested)
        {
            var high = Log.Signal.Latest;
            while (cursor < high)
            {
                var page = await Log.ReadAsync(query.Filter(grant, high) with { After = cursor, Limit = EventQuery.MaxLimit }, ct).ConfigureAwait(false);
                foreach (var e in page)
                    yield return e;
                cursor = page.Count < EventQuery.MaxLimit ? high : page[^1].Seq;
            }
            var woken = await Wake(high, ct).ConfigureAwait(false);
            if (!woken)
                yield break;
        }
    }

    /// <summary><c>GET /v1/threads</c>.</summary>
    public async Task<EventAnswer> ThreadsAsync(EventGrant grant, string? state, string? scope, int? limit, CancellationToken ct)
    {
        if (state is not (null or "open" or "closed" or "all"))
            return Refuse(new EventRefusal(400, "bad_state", "'state' is open, closed or all"), counted: false);
        var cards = await Threads.CardsAsync(Log, grant, ct).ConfigureAwait(false);
        var wanted = cards
            .Where(c => (state ?? "open") == "all" || c.State == (state ?? "open"))
            .Where(c => scope is null || Glob.Match(scope, c.Scope))
            .OrderByDescending(c => c.LastActivity, StringComparer.Ordinal).ThenByDescending(c => c.Last.Seq)
            .Take(Math.Clamp(limit ?? Threads.DefaultLimit, 1, Threads.MaxLimit));
        return new EventAnswer(200, Json(w =>
        {
            w.WriteStartArray("threads");
            foreach (var card in wanted)
                card.Write(w, withText: false);
            w.WriteEndArray();
        }));
    }

    /// <summary><c>GET /v1/threads/{id}/card</c>: 404 for a thread that is unknown or outside the grants, so a caller cannot probe for one.</summary>
    public async Task<EventAnswer> CardAsync(EventGrant grant, string thread, CancellationToken ct)
    {
        if (await Threads.CardAsync(Log, grant, thread, ct).ConfigureAwait(false) is not { } card)
            return Refuse(new EventRefusal(404, "no_thread", "no such thread"), counted: false);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
            card.Write(w, withText: true);
        return new EventAnswer(200, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private async Task<int?> FirstStepWithoutGoal(List<JsonElement> items, List<NewEvent> batch, CancellationToken ct)
    {
        var started = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < batch.Count; i++)
        {
            if (batch[i].Type != ThreadStep.Type)
                continue;
            var thread = batch[i].Subject!;
            if (ThreadStep.HasGoal(items[i]))
            {
                started.Add(thread);
                continue;
            }
            if (started.Contains(thread))
                continue;
            var known = await Log.ReadAsync(new EventFilter(Type: ThreadStep.Type, Subject: thread, Limit: 1), ct).ConfigureAwait(false);
            if (known.Count == 0)
                return i;
            started.Add(thread);
        }
        return null;
    }

    private async Task<bool> Wake(long after, CancellationToken ct)
    {
        try
        {
            await Log.Signal.WaitAsync(after, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private EventAnswer Refuse(EventRefusal r, bool counted = true)
    {
        if (counted)
            Interlocked.Increment(ref refused);
        return new EventAnswer(r.Status, Error(r));
    }

    /// <summary>A refusal as the routes' error body; it names the field or grant, never the refused value.</summary>
    public static string Error(EventRefusal r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return Json(w =>
        {
            w.WriteStartObject("error");
            w.WriteString("code", r.Code);
            w.WriteString("message", r.Message);
            w.WriteNumber("index", r.Index);
            w.WriteEndObject();
        });
    }

    internal static string Json(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
