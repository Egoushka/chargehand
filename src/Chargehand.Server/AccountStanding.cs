using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;

namespace Chargehand.Server;

/// <summary>What <c>GET /v1/accounts/me</c> says: who the key is, its rules and its spend in the UTC day and month. Fields left out are no cap.</summary>
public sealed record AccountStandingBody(string Name, bool Owner, bool Driven, IReadOnlyList<string>? Repositories, AccountLimits Limits, StandingSpend Spend,
    int UnfinishedRuns, bool Halted, IReadOnlyList<string> Presets, SpendForecast? Forecast = null);

public sealed record AccountLimits(decimal? MaxUsdPerDay, decimal? MaxUsdPerMonth, long? MaxTokensPerDay, long? MaxTokensPerMonth, int? MaxConcurrentRuns);

public sealed record StandingSpend(WindowSpend Day, WindowSpend Month);

/// <param name="Usd">What an API key was billed (ADR 0082): a subscription call adds none, so a token cap is the bound that always binds.</param>
/// <param name="UsdAtApiPrice">Every call at the profile's price table, whoever paid; equals <paramref name="Usd"/> when no subscription ran.</param>
public sealed record WindowSpend(decimal Usd, long Tokens, DateTimeOffset ResetsAt, decimal UsdAtApiPrice = 0);

/// <summary>The caller's own standing (ADR 0054), from the same spend the caps are checked against (ADR 0050). The owner has no caps; its spend is that of runs recorded as owner.</summary>
public static class AccountStanding
{
    public static async Task<AccountStandingBody> ReadAsync(Principal who, RunService runs, IRunLog log, CancellationToken ct)
    {
        var now = runs.Now;
        var day = await log.SpendAsync(who.Name, AccountPolicy.DayStart(now), ct);
        var month = await log.SpendAsync(who.Name, AccountPolicy.MonthStart(now), ct);
        var trailing = await log.SpendAsync(who.Name, SpendForecasts.TrailingStart(now), ct);
        var a = who.Account;
        return new(who.Name, who.IsOwner, a?.Driven ?? true, a?.Repositories,
            new(a?.MaxUsdPerDay, a?.MaxUsdPerMonth, a?.MaxTokensPerDay, a?.MaxTokensPerMonth, a?.MaxConcurrentRuns),
            new(new(day.Usd, day.Tokens, AccountPolicy.NextDay(now), day.UsdAtApiPrice), new(month.Usd, month.Tokens, AccountPolicy.NextMonth(now), month.UsdAtApiPrice)),
            runs.Unfinished(who.Name), runs.Halted, runs.Presets(), SpendForecasts.Of(month.Usd, trailing.Usd, a?.MaxUsdPerMonth, now));
    }
}
