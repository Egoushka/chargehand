namespace Chargehand.Modules.Events.Api;

/// <summary>The threads derived from <c>thread.step.recorded</c> events on every call (threads addendum): nothing is stored that the events do not hold. A thread whose
/// latest step is outside the caller's read grants does not exist for it.</summary>
public static class Threads
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    private const int Page = 1000;

    /// <summary>Every thread the grant may see, as cards.</summary>
    public static async Task<IReadOnlyList<ThreadCard>> CardsAsync(IEventLog log, EventGrant grant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(log);
        var steps = await StepsAsync(log, null, ct).ConfigureAwait(false);
        var cards = new List<ThreadCard>();
        foreach (var thread in steps.GroupBy(s => s.Thread, StringComparer.Ordinal))
        {
            if (await CardOf(log, grant, [.. thread], ct).ConfigureAwait(false) is { } card)
                cards.Add(card);
        }
        return cards;
    }

    /// <summary>One thread's card, or null when it is unknown or outside the grant.</summary>
    public static async Task<ThreadCard?> CardAsync(IEventLog log, EventGrant grant, string thread, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(log);
        var steps = await StepsAsync(log, thread, ct).ConfigureAwait(false);
        return steps.Count == 0 ? null : await CardOf(log, grant, steps, ct).ConfigureAwait(false);
    }

    private static async Task<ThreadCard?> CardOf(IEventLog log, EventGrant grant, List<ThreadStep> steps, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (!grant.CanRead(steps[^1].Scope))
            return null;
        var readable = steps.Where(s => grant.CanRead(s.Scope)).ToList();
        var newest = await log.ReadAsync(new EventFilter(Thread: steps[^1].Thread, Readable: grant == EventGrant.Owner ? null : grant.Read, Limit: 1, Newest: true), ct)
            .ConfigureAwait(false);
        return ThreadCard.From(readable, newest.Count > 0 ? Latest(newest[0].Time, readable) : null);
    }

    /// <summary>The newest time among the newest event and the steps: <c>seq</c> order and <c>time</c> order can differ.</summary>
    private static string Latest(string time, List<ThreadStep> steps) =>
        steps.Select(s => s.Time).Append(time).Max(StringComparer.Ordinal)!;

    private static async Task<List<ThreadStep>> StepsAsync(IEventLog log, string? thread, CancellationToken ct)
    {
        var steps = new List<ThreadStep>();
        long after = 0;
        while (true)
        {
            var page = await log.ReadAsync(new EventFilter(after, Type: ThreadStep.Type, Subject: thread, Limit: Page), ct).ConfigureAwait(false);
            steps.AddRange(page.Select(ThreadStep.Read).OfType<ThreadStep>());
            if (page.Count < Page)
                return steps;
            after = page[^1].Seq;
        }
    }
}
