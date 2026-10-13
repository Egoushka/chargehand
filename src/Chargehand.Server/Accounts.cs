using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.RunLog;
using Microsoft.AspNetCore.Http;

namespace Chargehand.Server;

/// <summary>Who a request is (ADR 0050). The owner is the holder of <c>http.api_key_secret</c>; any other name is an account the profile declares, and
/// <paramref name="Account"/> holds its rules. No principal at all (stdio, the CLI) is unrestricted.</summary>
public sealed record Principal(string Name, AccountSettings? Account = null)
{
    /// <summary>Where the middleware leaves the request's principal.</summary>
    public const string ItemKey = "chargehand.principal";

    public static Principal Owner { get; } = new(AccountSettings.Owner);

    public bool IsOwner => Name == AccountSettings.Owner;

    public static Principal? Of(HttpContext? ctx) => ctx?.Items[ItemKey] as Principal;

    /// <summary>The owner sees every run; an account sees the runs it started. A run with no recorded actor predates accounts and is the owner's.</summary>
    public static bool Sees(Principal? who, string? actor) => who is null || who.IsOwner || who.Name == actor;
}

/// <summary>One key of an account, as stored: never the key itself, only a salted SHA-256 of its secret half.</summary>
/// <param name="Id">The public half of the key (<c>chk1.&lt;id&gt;.&lt;secret&gt;</c>), what <c>accounts list</c> and <c>revoke</c> name.</param>
public sealed record AccountKey(string Id, string Account, string? Label, string Salt, string Hash, DateTimeOffset Created, DateTimeOffset? Revoked = null);

/// <summary>
/// The keys of the profile's accounts (ADR 0050), in one JSON file the server reads and <c>chargehand accounts</c> writes. A key is 32 random bytes, so a salted
/// SHA-256 is enough to keep a leaked file from yielding keys; the file is readable by its owner only. The server re-reads the file when it changes, so a revoked key stops
/// working without a restart.
/// </summary>
public sealed class AccountKeyStore(string path)
{
    public const string Prefix = "chk1";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true };

    private readonly object _lock = new();
    private (DateTime Written, long Length) _stamp;
    private IReadOnlyList<AccountKey> _cached = [];

    /// <summary>Makes a key for <paramref name="account"/> and returns it with its id. This is the only time the key exists in the clear.</summary>
    public (string Id, string Key) Create(string account, string? label)
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (_lock)
            Save([.. List(), new AccountKey(id, account, label, salt, HashOf(salt, secret), DateTimeOffset.UtcNow)]);
        return (id, $"{Prefix}.{id}.{secret}");
    }

    /// <returns>False when no key has that id or it is already revoked.</returns>
    public bool Revoke(string id)
    {
        lock (_lock)
        {
            var keys = List().ToList();
            var at = keys.FindIndex(k => k.Id == id && k.Revoked is null);
            if (at < 0)
                return false;
            keys[at] = keys[at] with { Revoked = DateTimeOffset.UtcNow };
            Save(keys);
            return true;
        }
    }

    public IReadOnlyList<AccountKey> List()
    {
        lock (_lock)
        {
            if (!File.Exists(path))
                return _cached = [];
            var info = new FileInfo(path);
            if ((info.LastWriteTimeUtc, info.Length) != _stamp)
            {
                _cached = JsonSerializer.Deserialize<List<AccountKey>>(File.ReadAllText(path), Json) ?? [];
                _stamp = (info.LastWriteTimeUtc, info.Length);
            }
            return _cached;
        }
    }

    /// <summary>The live key a presented bearer value is, or null. The salted hash is computed and compared in constant time whether or not the id exists.</summary>
    public AccountKey? Authenticate(string presented)
    {
        var parts = presented.Split('.');
        if (parts.Length != 3 || parts[0] != Prefix || presented.Length > 200)
            return null;
        var known = List().FirstOrDefault(k => k.Id == parts[1]);
        var hash = HashOf(known?.Salt ?? Convert.ToHexStringLower(new byte[16]), parts[2]);
        var same = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(known?.Hash ?? new string('0', 64)));
        return same && known is { Revoked: null } ? known : null;
    }

    private static string HashOf(string salt, string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{salt}:{secret}")));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Written to a temporary file, flushed and renamed over the old one, readable by its owner only.</summary>
    private void Save(IReadOnlyList<AccountKey> keys)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new FileStream(temp, options))
        {
            JsonSerializer.Serialize(file, keys, Json);
            file.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
        _stamp = default;
    }
}

/// <summary>The accounts the profile declares, and the keys that open them. A key whose account the profile no longer declares opens nothing.</summary>
public sealed class AccountDirectory(IReadOnlyList<AccountSettings> declared, AccountKeyStore keys)
{
    public Principal? Authenticate(string bearer) =>
        keys.Authenticate(bearer) is { } key && declared.FirstOrDefault(a => a.Name == key.Account) is { } account ? new Principal(account.Name, account) : null;
}

/// <summary>Why an account's run was not started. <c>Window</c>, <c>Unit</c>, <c>Limit</c>, <c>Spent</c>, <c>Remaining</c> and <c>ResetsAt</c> are set for
/// <c>budget_exceeded</c> only.</summary>
public sealed record AccountRefusal(string Code, string Message, [property: JsonIgnore] int Status, string? Window = null, string? Unit = null, decimal? Limit = null, decimal? Spent = null,
    decimal? Remaining = null, DateTimeOffset? ResetsAt = null)
{
    [JsonIgnore]
    public object Body => new { errors = new[] { Message }, error = this };
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

    public static AccountRefusal? Refusal(AccountSettings account, RunRequest request, int unfinished, ActorSpend day, ActorSpend month, DateTimeOffset now)
    {
        if (AccountCredentials.Refusal(account) is { } noCredential)
            return new("model_credential_missing", noCredential, StatusCodes.Status403Forbidden);
        if (request.Driven is not null && !account.Driven)
            return new("driven_not_allowed", $"account '{account.Name}' may not start a batch (a driven block)", StatusCodes.Status403Forbidden);
        if (account.Repositories is { Count: > 0 } roots)
        {
            if (request.Context.Repository is not { } repository)
                return new("repository_not_allowed", $"account '{account.Name}' must name a repository in context.repository", StatusCodes.Status403Forbidden);
            if (!roots.Any(r => Within(r, repository.Path)))
                return new("repository_not_allowed", $"account '{account.Name}' may not use the repository {repository.Path}", StatusCodes.Status403Forbidden);
        }
        if (account.MaxConcurrentRuns is { } most && unfinished >= most)
            return new("concurrency_exceeded", $"account '{account.Name}' has {unfinished} unfinished runs, its limit; retry when one ends", StatusCodes.Status429TooManyRequests);
        return Exceeded("day", "usd", account.MaxUsdPerDay, day.Usd, NextDay(now))
            ?? Exceeded("month", "usd", account.MaxUsdPerMonth, month.Usd, NextMonth(now))
            ?? Exceeded("day", "tokens", account.MaxTokensPerDay, day.Tokens, NextDay(now))
            ?? Exceeded("month", "tokens", account.MaxTokensPerMonth, month.Tokens, NextMonth(now));
    }

    /// <summary>Spend counts runs that finished in the window and the calls of those still going; a cap is met once the spend reaches it.</summary>
    private static AccountRefusal? Exceeded(string window, string unit, decimal? limit, decimal spent, DateTimeOffset resets)
    {
        if (limit is not { } cap || spent < cap)
            return null;
        var amount = unit == "usd" ? $"${cap.ToString("0.00", CultureInfo.InvariantCulture)}" : $"{cap.ToString("0", CultureInfo.InvariantCulture)} tokens";
        var spentText = unit == "usd" ? $"${spent.ToString("0.00", CultureInfo.InvariantCulture)}" : $"{spent.ToString("0", CultureInfo.InvariantCulture)} tokens";
        return new("budget_exceeded", $"the {(window == "day" ? "daily" : "monthly")} {(unit == "usd" ? "dollar" : "token")} cap of {amount} is used up ({spentText} spent, 0 remaining); it resets at {resets:yyyy-MM-dd'T'HH:mm:ss'Z'}",
            StatusCodes.Status429TooManyRequests, window, unit, cap, spent, 0, resets);
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

    private static bool Within(string root, string path)
    {
        var (r, p) = (Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        return p == r || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
