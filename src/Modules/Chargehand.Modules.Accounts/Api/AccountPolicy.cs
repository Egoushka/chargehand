using System.Globalization;
using System.Text.Json.Serialization;
using Chargehand.Contracts;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Modules.Accounts.Api;

/// <summary>Why an account's run was not started. <c>Window</c>, <c>Unit</c>, <c>Limit</c>, <c>Spent</c>, <c>Remaining</c> and <c>ResetsAt</c> are set for
/// <c>budget_exceeded</c> only.</summary>
public sealed record AccountRefusal(string Code, string Message, [property: JsonIgnore] int Status, string? Window = null, string? Unit = null, decimal? Limit = null, decimal? Spent = null,
    decimal? Remaining = null, DateTimeOffset? ResetsAt = null, decimal? Reserved = null)
{
    public const int Forbidden = 403;

    public const int TooManyRequests = 429;
}

/// <summary>What an account may start (ADR 0050), decided at request time from the account's rules, its unfinished runs and its spend in the UTC day and month. Pure: the caller
/// reads the spend.</summary>
public static class AccountPolicy
{
    public static DateTimeOffset DayStart(DateTimeOffset now) => new(now.UtcDateTime.Date, TimeSpan.Zero);

    public static DateTimeOffset MonthStart(DateTimeOffset now) => new(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset NextDay(DateTimeOffset now) => DayStart(now).AddDays(1);

    public static DateTimeOffset NextMonth(DateTimeOffset now) => MonthStart(now).AddMonths(1);

    public static bool UsesDay(AccountSettings a) => a.MaxUsdPerDay is not null || a.MaxTokensPerDay is not null;

    public static bool UsesMonth(AccountSettings a) => a.MaxUsdPerMonth is not null || a.MaxTokensPerMonth is not null;

    /// <summary>Why <paramref name="account"/> may not start a run for want of a model credential (ADR 0053), or null when it has one to run on.</summary>
    public static string? CredentialRefusal(AccountSettings account) => account.Model is null && !account.AllowServerCredential
        ? $"account '{account.Name}' has no model credential of its own and may not use the server's; set http.accounts[].model, or allow_server_credential to spend the server's"
        : null;

    public static AccountRefusal? Refusal(AccountSettings account, RunRequest request, int unfinished, ActorSpend day, ActorSpend month, DateTimeOffset now,
        decimal reservedUsd = 0, SubscriptionStanding? subscription = null)
    {
        if (CredentialRefusal(account) is { } noCredential)
            return new("model_credential_missing", noCredential, AccountRefusal.Forbidden);
        if (request.Driven is not null && !account.Driven)
            return new("driven_not_allowed", $"account '{account.Name}' may not start a batch (a driven block)", AccountRefusal.Forbidden);
        if (account.Repositories is { Count: > 0 } roots)
        {
            if (request.Context.Repository is not { } repository)
                return new("repository_not_allowed", $"account '{account.Name}' must name a repository in context.repository", AccountRefusal.Forbidden);
            if (!roots.Any(r => Within(r, repository.Path)))
                return new("repository_not_allowed", $"account '{account.Name}' may not use the repository {repository.Path}", AccountRefusal.Forbidden);
            if (account.Model is { ThirdParty: true } && AccountSettings.HasWorkSegment(RealPath(repository.Path)))
                return new("repository_not_allowed", $"account '{account.Name}' uses a third-party model, which never sees a work directory: {repository.Path}", AccountRefusal.Forbidden);
        }
        if (account.MaxConcurrentRuns is { } most && unfinished >= most)
            return new("concurrency_exceeded", $"account '{account.Name}' has {unfinished} unfinished runs, its limit; retry when one ends", AccountRefusal.TooManyRequests);
        return Exceeded("day", "usd", account.MaxUsdPerDay, day.Usd, NextDay(now), reservedUsd)
            ?? Exceeded("month", "usd", account.MaxUsdPerMonth, month.Usd, NextMonth(now), reservedUsd)
            ?? Exceeded("day", "tokens", account.MaxTokensPerDay, day.Tokens, NextDay(now))
            ?? Exceeded("month", "tokens", account.MaxTokensPerMonth, month.Tokens, NextMonth(now))
            ?? SubscriptionCap.Refusal(account, subscription, now);
    }

    /// <summary>Spend counts runs that finished in the window and the calls of those still going; a cap is met once the spend reaches it.</summary>
    private static AccountRefusal? Exceeded(string window, string unit, decimal? limit, decimal spent, DateTimeOffset resets, decimal reserved = 0)
    {
        if (limit is not { } cap || spent < cap)
            return null;
        var amount = unit == "usd" ? $"${cap.ToString("0.00", CultureInfo.InvariantCulture)}" : $"{cap.ToString("0", CultureInfo.InvariantCulture)} tokens";
        // The caller adds the unfinished runs' budgets to the spend (ADR 0083); the message tells the two apart (ADR 0100).
        var billed = spent - reserved;
        var spentText = unit == "usd" ? $"${billed.ToString("0.00", CultureInfo.InvariantCulture)}" : $"{spent.ToString("0", CultureInfo.InvariantCulture)} tokens";
        var held = reserved > 0 ? $", ${reserved.ToString("0.00", CultureInfo.InvariantCulture)} reserved by unfinished runs" : "";
        return new("budget_exceeded", $"the {(window == "day" ? "daily" : "monthly")} {(unit == "usd" ? "dollar" : "token")} cap of {amount} is used up ({spentText} spent{held}, 0 remaining); it resets at {resets:yyyy-MM-dd'T'HH:mm:ss'Z'}",
            AccountRefusal.TooManyRequests, window, unit, cap, billed, 0, resets, unit == "usd" ? reserved : null);
    }

    /// <summary>The request with its budgets cut to what the account has left, so one run cannot take more than the window holds: <c>budget_usd</c>, and for a batch
    /// <c>max_usd_total</c> and <c>max_tokens_total</c>. A caller that asked for less keeps it.</summary>
    public static RunRequest Clamp(AccountSettings account, RunRequest request, ActorSpend day, ActorSpend month)
    {
        decimal?[] usd = [account.MaxUsdPerDay - day.Usd, account.MaxUsdPerMonth - month.Usd];
        decimal?[] tokens = [account.MaxTokensPerDay - day.Tokens, account.MaxTokensPerMonth - month.Tokens];
        var leftUsd = usd.Where(u => u is not null).Min();
        var leftTokens = tokens.Where(t => t is not null).Min();
        if (leftUsd is { } u && request.Context.BudgetUsd > u)
            request = request with { Context = request.Context with { BudgetUsd = u } };
        if (request.Driven is { } driven)
            request = request with
            {
                Driven = driven with
                {
                    MaxUsdTotal = leftUsd is { } du ? Math.Min(driven.MaxUsdTotal ?? du, du) : driven.MaxUsdTotal,
                    MaxTokensTotal = leftTokens is { } dt ? (long)Math.Min(driven.MaxTokensTotal ?? dt, dt) : driven.MaxTokensTotal,
                },
            };
        return request;
    }

    /// <summary><paramref name="path"/> with every symlink on it followed, so a link inside an allowed root cannot lead to a directory the account must not see.</summary>
    private static string RealPath(string path)
    {
        var current = Path.GetPathRoot(Path.GetFullPath(path)) ?? "/";
        foreach (var segment in Path.GetFullPath(path)[current.Length..].Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (new DirectoryInfo(current).ResolveLinkTarget(true) is { } target)
                current = target.FullName;
        }
        return current;
    }

    private static bool Within(string root, string path)
    {
        var (r, p) = (Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        return p == r || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
