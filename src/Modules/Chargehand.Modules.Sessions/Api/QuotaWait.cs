namespace Chargehand.Modules.Sessions.Api;

/// <summary>Whether a run that ended on the subscription's rate limit waits for the reset and resumes, and for how long (ADR 0096). Pure: the caller passes the time.</summary>
public static class QuotaWait
{
    /// <summary>The longest one wait: the scheduler's own pause for a limit (ADR 0045).</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromHours(6);

    /// <summary>Added to the reset so a skewed clock does not meet the limit again.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(30);

    /// <summary>How many times one run waits; a further limit ends it as before.</summary>
    public const int MostWaits = 4;

    /// <summary>The turn a resumed session is given.</summary>
    public const string ResumePrompt = "The usage limit has lifted. Carry on with the task from where you stopped.";

    /// <summary>How long to wait before resuming, or null when the run should end as it did before: the result is not an error, is not a rate limit, is a spend budget's refusal (its
    /// reset is a day away and the scheduler halts starts for it), the run has waited <see cref="MostWaits"/> times, or the wait would exceed <paramref name="room"/> or <see cref="Longest"/>.</summary>
    /// <param name="room">The longest this run may wait now (the caller's own bound, such as the time left on its wall clock).</param>
    public static TimeSpan? Delay(bool isError, string? text, int? apiStatus, DateTimeOffset now, int waited, TimeSpan room)
    {
        if (!isError || waited >= MostWaits || GatewayRefusal.IsBudget(text) || !(apiStatus == 429 || UsageLimit.Matches(text)))
            return null;
        var until = GatewayRefusal.ResetsAt(text, now) is { } at && at > now ? at - now : UsageLimit.DefaultBackoff;
        var delay = until + Margin;
        return delay <= room && delay <= Longest ? delay : null;
    }
}
