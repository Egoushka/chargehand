using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Runtimes.ClaudeCode.Api;

/// <summary>Subscription first, then the API credit (issue #632): calls go on the subscription; a subscription rate limit marks it limited until its reset, and while it is
/// limited a call may go on the API fallback key if today's fallback spend (UTC day) is under <paramref name="dailyUsd"/>. The first call after the reset goes on the subscription
/// again. A gateway's budget refusal on the fallback closes it until the next UTC day. A call is checked before it starts, so the cap can be passed by at most the calls in flight.
/// The day's spend, the refusal and the reset outlive a restart in <paramref name="ledgerFile"/> (null: memory only).</summary>
/// <remarks>The cap counts chargehand's own fallback spend. When the fallback key is shared, the shared cap is the gateway's budget on that key; this class sees it only as a refusal.</remarks>
public sealed class ApiFallback(decimal dailyUsd, string? ledgerFile, TimeProvider? time = null)
{
    private readonly object _lock = new();
    private FallbackLedger? _ledger;

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

    // A new UTC day starts at zero spend and no refusal; the subscription's reset carries over.
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
public sealed record FallbackLedger(DateOnly Day, decimal SpentUsd, bool Refused, DateTimeOffset? LimitedUntil);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(FallbackLedger))]
internal sealed partial class ApiFallbackJsonContext : JsonSerializerContext;
