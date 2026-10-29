using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Runtimes.ClaudeCode.Api;

/// <summary>Subscription first, then the API credit (issue #632): calls go on the subscription; a subscription rate limit marks it limited until its reset, and while it is
/// limited a call may go on the API fallback key if today's fallback spend (UTC day) is under <paramref name="dailyUsd"/>. The first call after the reset goes on the subscription
/// again. A gateway's budget refusal on the fallback closes it until the next UTC day. A call is checked before it starts, so the cap can be passed by at most the calls in flight.
/// The day's spend, the refusal and the reset outlive a restart in <paramref name="ledgerFile"/> (null: memory only).</summary>
/// <remarks>Driven batches spend the fallback in their egress container, out of reach of <see cref="Spent"/> until they end (ADR 0088): <see cref="Reserve"/> sets a share of what is left
/// aside at a batch's start and <see cref="Settle"/> replaces it with what the batch spent. The cap counts chargehand's own fallback spend. When the fallback key is shared, the shared cap is the gateway's budget on that key; this class sees it only as a refusal.</remarks>
public sealed class ApiFallback(decimal dailyUsd, string? ledgerFile, TimeProvider? time = null)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ApiFallback> Shared = new(StringComparer.Ordinal);

    private readonly object _lock = new();
    private FallbackLedger? _ledger;

    /// <summary>The one gate for a ledger file in this process: worker turns and driven batches spend one cap (ADR 0088), and two gates over a file would each overwrite the other's day.</summary>
    public static ApiFallback For(decimal dailyUsd, string ledgerFile) =>
        Shared.GetOrAdd(Path.GetFullPath(ledgerFile), file => new ApiFallback(dailyUsd, file));

    public decimal DailyUsd => dailyUsd;

    /// <summary>The next call goes on the fallback: the subscription is limited and today's fallback is open and under the cap.</summary>
    public bool UseFallback()
    {
        lock (_lock)
        {
            var l = Today();
            return l.LimitedUntil is { } until && Now < until && Open(l);
        }
    }

    /// <summary>The subscription hit its limit; it stays limited until <paramref name="resetsAt"/> (a later reset already known wins). True when the call may be rerun on the fallback.</summary>
    public bool Limited(DateTimeOffset resetsAt)
    {
        lock (_lock)
        {
            var l = Today();
            Save(l with { LimitedUntil = l.LimitedUntil is { } known && known > resetsAt ? known : resetsAt });
            return Open(_ledger!);
        }
    }

    /// <summary>A call on the fallback cost <paramref name="usd"/>.</summary>
    public void Spent(decimal usd)
    {
        if (usd <= 0)
            return;
        lock (_lock)
        {
            var l = Today();
            Save(l with { SpentUsd = l.SpentUsd + usd });
        }
    }

    /// <summary>Sets aside the share of today's fallback that batch <paramref name="batch"/> may spend in its egress container: half of what is left of the cap after today's spend and what other
    /// batches hold. Reservations therefore never pass the cap, and a batch that starts while others run still gets a share. A batch that holds a reservation already (it resumed after a restart)
    /// gets that one back.</summary>
    /// <returns>The allowance in dollars; 0 when the fallback is closed, spent or fully reserved, in which case nothing is held.</returns>
    public decimal Reserve(string batch)
    {
        lock (_lock)
        {
            var l = Today();
            if (l.Reserved is { } held && held.TryGetValue(batch, out var mine))
                return mine;
            if (!Open(l))
                return 0;
            var others = l.Reserved ?? [];
            var unreserved = dailyUsd - l.SpentUsd - others.Values.Sum();
            if (unreserved <= 0)
                return 0;
            var share = Math.Round(unreserved / 2, 6);
            Save(l with { Reserved = new Dictionary<string, decimal>(others) { [batch] = share } });
            return share;
        }
    }

    /// <summary>A batch ended: its reservation is released and what it spent (read from its egress log) is added to today's spend. <paramref name="refused"/>: the gateway answered the
    /// fallback with 402; <paramref name="resetsAt"/>: when the subscription's limit lifts, if the batch saw one.</summary>
    public void Settle(string batch, decimal usd, bool refused, DateTimeOffset? resetsAt)
    {
        lock (_lock)
        {
            Release(batch);
            if (usd > 0)
                Save(Today() with { SpentUsd = Today().SpentUsd + usd });
            if (refused)
                Save(Today() with { Refused = true });
            if (resetsAt is { } at)
            {
                var l = Today();
                Save(l with { LimitedUntil = l.LimitedUntil is { } known && known > at ? known : at });
            }
        }
    }

    /// <summary>A batch ended and its egress log could not be read: the whole reservation counts as spent, so the cap errs low.</summary>
    public void SettleUnreadable(string batch)
    {
        lock (_lock)
        {
            var held = Today().Reserved is { } r && r.TryGetValue(batch, out var mine) ? mine : 0;
            Settle(batch, held, refused: false, resetsAt: null);
        }
    }

    private void Release(string batch)
    {
        var l = Today();
        if (l.Reserved is not { } held || !held.ContainsKey(batch))
            return;
        var rest = new Dictionary<string, decimal>(held);
        rest.Remove(batch);
        Save(l with { Reserved = rest.Count == 0 ? null : rest });
    }

    /// <summary>The gateway refused the fallback over its budget: no more fallback today.</summary>
    public void Refused()
    {
        lock (_lock)
            Save(Today() with { Refused = true });
    }

    /// <summary>Today's spend, refusal and the subscription's reset, as the gate holds them.</summary>
    public FallbackLedger Read()
    {
        lock (_lock)
            return Today();
    }

    private DateTimeOffset Now => (time ?? TimeProvider.System).GetUtcNow();

    private bool Open(FallbackLedger l) => !l.Refused && l.SpentUsd < dailyUsd;

    // A new UTC day starts at zero spend, no refusal and no reservations (the day's cap is new, and a batch that never settled cannot hold it); the subscription's reset carries over.
    private FallbackLedger Today()
    {
        var day = DateOnly.FromDateTime(Now.UtcDateTime);
        _ledger ??= Load() ?? new FallbackLedger(day, 0, false, null);
        if (_ledger.Day != day)
            _ledger = new FallbackLedger(day, 0, false, _ledger.LimitedUntil);
        return _ledger;
    }

    private FallbackLedger? Load()
    {
        try
        {
            return ledgerFile is not null && File.Exists(ledgerFile) ? JsonSerializer.Deserialize(File.ReadAllText(ledgerFile), ApiFallbackJsonContext.Default.FallbackLedger) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Written beside and renamed, so a read never sees half a file; a failed write keeps the gate in memory and only loses the ledger across a restart.
    private void Save(FallbackLedger ledger)
    {
        _ledger = ledger;
        if (ledgerFile is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ledgerFile))!);
            var temp = $"{ledgerFile}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(ledger, ApiFallbackJsonContext.Default.FallbackLedger));
            File.Move(temp, ledgerFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <param name="Day">The UTC day <paramref name="SpentUsd"/> and <paramref name="Refused"/> belong to.</param>
/// <param name="LimitedUntil">When the subscription's last limit lifts; null when none was seen.</param>
/// <param name="Reserved">Dollars set aside per running batch id (ADR 0088); null when none.</param>
public sealed record FallbackLedger(DateOnly Day, decimal SpentUsd, bool Refused, DateTimeOffset? LimitedUntil, Dictionary<string, decimal>? Reserved = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(FallbackLedger))]
[JsonSerializable(typeof(Dictionary<string, decimal>))]
internal sealed partial class ApiFallbackJsonContext : JsonSerializerContext;
