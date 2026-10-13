using Chargehand.Config;

namespace Chargehand.Server;

/// <summary>An account key as the key file keeps it, without its salt or hash. <c>Id</c> is the public half (<c>chk1.&lt;id&gt;.&lt;secret&gt;</c>), which <c>chargehand accounts revoke</c> takes.</summary>
public sealed record KeyBody(string Id, string? Label, DateTimeOffset Created, DateTimeOffset? Revoked);

/// <param name="Mode"><c>own_key</c> (an API key of its own), <c>own_gateway</c> (a gateway's key and base URL), <c>server</c> (the server's credential, allowed), <c>none</c> (refused at admission), or for the owner <c>server</c>.</param>
/// <param name="Present">Whether the secret item the profile names resolves right now. Null where no item is named. Never the value.</param>
public sealed record CredentialBody(string Mode, bool? Present);

/// <param name="Repositories">Directories the account may name; null: any the server allows.</param>
public sealed record AccountBody(string Name, bool Owner, bool Driven, IReadOnlyList<string>? Repositories, AccountLimits Limits, StandingSpend Spend, int UnfinishedRuns, CredentialBody Credential,
    IReadOnlyList<KeyBody> Keys);

/// <summary><c>accounts/v1</c>: the owner and every account the profile declares, with caps, spend against them, and the state of their keys and credentials. Owner only.</summary>
public sealed record AccountsBody(string ContractVersion, DateTimeOffset GeneratedAt, IReadOnlyList<AccountBody> Accounts, IReadOnlyList<string> NotRecorded);

internal static class ConsoleAccounts
{
    public const string Contract = "accounts/v1";

    private static readonly IReadOnlyList<string> NotRecorded =
    [
        "A key's last use is not recorded.",
        "'present' says the secret item resolves, not that the credential works; chargehand doctor and a run find that out.",
    ];

    public static async Task<AccountsBody> BuildAsync(ConsoleContext c, AccountKeyStore? keys, CancellationToken ct)
    {
        var now = c.Now;
        var stored = keys?.List() ?? [];
        var declared = c.Settings.Accounts ?? [];
        List<AccountBody> accounts = [];
        foreach (var name in declared.Select(a => a.Name).Prepend(AccountSettings.Owner))
        {
            var account = declared.FirstOrDefault(a => a.Name == name);
            var day = await c.Log.SpendAsync(name, AccountPolicy.DayStart(now), ct);
            var month = await c.Log.SpendAsync(name, AccountPolicy.MonthStart(now), ct);
            accounts.Add(new AccountBody(name, account is null, account?.Driven ?? true, account?.Repositories,
                new AccountLimits(account?.MaxUsdPerDay, account?.MaxUsdPerMonth, account?.MaxTokensPerDay, account?.MaxTokensPerMonth, account?.MaxConcurrentRuns),
                new StandingSpend(new WindowSpend(day.Usd, day.Tokens, AccountPolicy.NextDay(now)), new WindowSpend(month.Usd, month.Tokens, AccountPolicy.NextMonth(now))),
                c.Runs.Unfinished(name), Credential(c, account),
                [.. stored.Where(k => k.Account == name).OrderBy(k => k.Created).Select(k => new KeyBody(k.Id, k.Label, k.Created, k.Revoked))]));
        }
        return new AccountsBody(Contract, now, accounts, NotRecorded);
    }

    private static CredentialBody Credential(ConsoleContext c, AccountSettings? account)
    {
        if (account is null)
            return new("server", null);
        if (account.Model is { ApiKeySecret: { } item } model)
            return new(model.BaseUrl is null ? "own_key" : "own_gateway", c.SecretPresent(item));
        return new(account.AllowServerCredential ? "server" : "none", null);
    }
}
