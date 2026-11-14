using System.Globalization;
using Chargehand.Contracts;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Modules.Accounts.Api;

/// <summary>One window's standing against its daily cap (ADR 0100). <paramref name="State"/> is a <see cref="SubscriptionCap"/> constant; <paramref name="UsedPct"/> is set when measured.</summary>
public sealed record WindowCap(string State, decimal? CapPct = null, decimal? UsedPct = null);

/// <summary>The share of the subscription an account's runs have used today, per window. <paramref name="State"/> is <c>measured</c>, <c>no_reading</c>, <c>not_measurable</c>
/// or <c>uncapped</c> (no window has a cap, or the account does not run on a subscription).</summary>
public sealed record SubscriptionStanding(string State, string? Provider, WindowCap SevenDay, WindowCap FiveHour);

/// <summary>The daily percent caps of a subscription (ADR 0100): pure rules over the readings a log holds.</summary>
public static class SubscriptionCap
{
    public const string Measured = "measured";
    public const string NoReading = "no_reading";
    public const string NotMeasurable = "not_measurable";
    public const string Uncapped = "uncapped";

    /// <summary>Points of a window used in the day: per distinct reset, the last reading minus the first (the first reset) or minus 0 (a later one began at 0); the resets add.</summary>
    public static decimal? UsedPoints(IReadOnlyList<SubscriptionReading> readings, Func<SubscriptionUsage, UsageWindow?> window)
    {
        var seen = readings.Select(r => (r.At, W: window(r.Usage))).Where(r => r.W is not null).OrderBy(r => r.At).ToList();
        if (seen.Count == 0)
            return null;
        var groups = seen.GroupBy(r => r.W!.ResetsAt).OrderBy(g => g.First().At).ToList();
        var total = 0d;
        for (var i = 0; i < groups.Count; i++)
        {
            var last = groups[i].Last().W!.Utilization;
            total += i == 0 ? last - groups[i].First().W!.Utilization : last;
        }
        return (decimal)Math.Max(0, total * 100);
    }

    /// <summary>The account's standing; null when it sets no percent cap. <paramref name="credential"/> and <paramref name="provider"/> come from the account's runtime.</summary>
    public static async Task<SubscriptionStanding?> ReadAsync(AccountSettings? account, string? credential, string? provider, IRunLog? log, DateTimeOffset now, CancellationToken ct)
    {
        if (account is not { } a || (a.MaxPctPerDay7d is null && a.MaxPctPerDay5h is null))
            return null;
        WindowCap Off(decimal? cap) => new(Uncapped, cap);
        if (credential != CredentialKind.Subscription)
            return new(Uncapped, null, Off(a.MaxPctPerDay7d), Off(a.MaxPctPerDay5h));
        if (provider is null)
            return new(NotMeasurable, null, Of(a.MaxPctPerDay7d, NotMeasurable), Of(a.MaxPctPerDay5h, NotMeasurable));
        var readings = log is null ? [] : await log.SubscriptionReadingsAsync(a.Name, AccountPolicy.DayStart(now), ct);
        var seven = Window(a.MaxPctPerDay7d, UsedPoints(readings, u => u.SevenDay));
        var five = Window(a.MaxPctPerDay5h, UsedPoints(readings, u => u.FiveHour));
        var state = new[] { seven, five }.Any(w => w.State == Measured) ? Measured : new[] { seven, five }.Any(w => w.State == NoReading) ? NoReading : Uncapped;
        return new(state, provider, seven, five);
    }

    private static WindowCap Of(decimal? cap, string state) => cap is null ? new(Uncapped) : new(state, cap);

    private static WindowCap Window(decimal? cap, decimal? used) => cap is null ? new(Uncapped) : used is null ? new(NoReading, cap) : new(Measured, cap, used);

    /// <summary>Why a start waits for the subscription's daily share, or null. A capped window with no reading today does not refuse: nothing says it is used, and the standing shows <c>no_reading</c> (ADR 0100).</summary>
    public static AccountRefusal? Refusal(AccountSettings account, SubscriptionStanding? standing, DateTimeOffset now)
    {
        if (standing is null)
            return null;
        var resets = AccountPolicy.NextDay(now);
        foreach (var (name, w, label) in new[] { ("7d", standing.SevenDay, "seven-day"), ("5h", standing.FiveHour, "five-hour") })
        {
            var provider = standing.Provider is null ? "subscription" : char.ToUpperInvariant(standing.Provider[0]) + standing.Provider[1..];
            switch (w.State)
            {
                case NotMeasurable:
                    return new("subscription_not_measurable", $"account '{account.Name}' has a daily subscription cap but its runtime reports no usage windows, so the cap cannot be measured; unset max_pct_per_day_{name} or run it on claude_code",
                        AccountRefusal.TooManyRequests);
                case Measured when w.UsedPct >= w.CapPct:
                    return new("subscription_cap_exceeded", $"the daily subscription cap of {Pct(w.CapPct!.Value)}% of the {provider} {label} window is used up ({Pct(w.UsedPct!.Value)} points used, 0 remaining); it resets at {resets:yyyy-MM-dd'T'HH:mm:ss'Z'}",
                        AccountRefusal.TooManyRequests, "day", "pct_" + name, w.CapPct, w.UsedPct, 0, resets);
            }
        }
        return null;
    }

    private static string Pct(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
