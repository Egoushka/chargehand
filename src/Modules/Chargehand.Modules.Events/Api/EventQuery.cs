namespace Chargehand.Modules.Events.Api;

/// <summary>The filters of <c>GET /v1/events</c> and its stream (events spec, decision 3). The globs follow <see cref="Glob"/>.</summary>
public sealed record EventQuery(long After = 0, string? Type = null, string? Source = null, string? Scope = null, string? Subject = null, int Limit = EventQuery.DefaultLimit)
{
    public const int DefaultLimit = 100;

    public const int MaxLimit = 500;

    /// <summary>The store's filter for <paramref name="grant"/>: an event outside its read grants is never returned.</summary>
    public EventFilter Filter(EventGrant grant, long? upTo = null)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return new EventFilter(After, upTo, Type, Source, Scope, Subject, Readable: grant == EventGrant.Owner ? null : grant.Read,
            Limit: Math.Clamp(Limit, 1, MaxLimit));
    }

    /// <summary>An exact scope the grant may not read gets 403 (decision 8); a glob is intersected with the grants instead.</summary>
    public EventRefusal? Problem(EventGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (Scope is { } scope && !Glob.IsPattern(scope) && !grant.CanRead(scope))
            return new EventRefusal(403, "scope_not_granted", $"token '{grant.Name}' may not read scope '{scope}'");
        return After < 0 ? new EventRefusal(400, "bad_after", "'after' is a seq, 0 or more") : null;
    }
}
