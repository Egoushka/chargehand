namespace Chargehand.Contracts;

/// <summary>One of a subscription's rate-limit windows as the provider reported it (ADR 0098). <paramref name="Utilization"/> is the fraction of the window used, as the provider
/// states it: 0.42 is 42 %, and a value above 1 means the usage ran past the window's cap. <paramref name="ResetsAt"/> is when the window resets; null when it was not reported.</summary>
public sealed record UsageWindow(double Utilization, DateTimeOffset? ResetsAt = null);

/// <summary>How much of a subscription's usage windows a run or call had used, read from what the provider reported while it ran (ADR 0098). A share, never dollars: a
/// subscription has no price per token. An instance holds at least one window; "no reading" is a null <c>SubscriptionUsage</c>, never a zero. <see cref="Used"/> is the larger
/// of the two windows, the one that would limit the account first, and <see cref="Window"/> names it.</summary>
public sealed record SubscriptionUsage(UsageWindow? FiveHour = null, UsageWindow? SevenDay = null)
{
    public const string FiveHourName = "five_hour";
    public const string SevenDayName = "seven_day";

    /// <summary>The larger window's utilization (a fraction of 1); null when neither window was read. Written to the record so a reader needs no arithmetic.</summary>
    public double? Used => (FiveHour, SevenDay) switch
    {
        (null, null) => null,
        ({ } five, null) => five.Utilization,
        (null, { } seven) => seven.Utilization,
        var (five, seven) => Math.Max(five!.Utilization, seven!.Utilization),
    };

    /// <summary>Which window <see cref="Used"/> is: <c>five_hour</c> or <c>seven_day</c>; null when neither was read. The weekly window wins a tie.</summary>
    public string? Window => (FiveHour, SevenDay) switch
    {
        (null, null) => null,
        (not null, null) => FiveHourName,
        (null, not null) => SevenDayName,
        var (five, seven) => five!.Utilization > seven!.Utilization ? FiveHourName : SevenDayName,
    };

    /// <summary>A reading of at most these two windows, or null when it holds none.</summary>
    public static SubscriptionUsage? Of(UsageWindow? fiveHour, UsageWindow? sevenDay) =>
        fiveHour is null && sevenDay is null ? null : new(fiveHour, sevenDay);

    /// <summary>The <paramref name="older"/> reading with the windows <paramref name="newer"/> carries laid over it: a window the newer one does not mention keeps its earlier value.</summary>
    public static SubscriptionUsage? Merge(SubscriptionUsage? older, SubscriptionUsage? newer) =>
        newer is null ? older : Of(newer.FiveHour ?? older?.FiveHour, newer.SevenDay ?? older?.SevenDay);
}
