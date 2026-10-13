using System.Globalization;
using Chargehand.Config;

namespace Chargehand.Server;

/// <summary><c>chargehand accounts add|list|revoke</c> (ADR 0050): the keys of the accounts the profile's <c>http.accounts</c> declares. Exit codes: 0 done, 1 not found, 2 usage.</summary>
public static class AccountsCli
{
    public const string Usage = """
        usage: chargehand accounts add <account> [--label <text>]   makes a key for a declared account; prints it once, stores only a salted hash
               chargehand accounts list                              the accounts, their caps and their keys (never the keys themselves)
               chargehand accounts revoke <key-id>                   the key stops working at once; the server needs no restart
        """;

    /// <summary>The keys file sits beside the run log, where the server finds it.</summary>
    public static string KeysFile(string runLogPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(runLogPath))!, "accounts.json");

    /// <param name="args">What follows <c>accounts</c>.</param>
    /// <remarks>An added key goes to <paramref name="output"/> alone, so a script can capture it; everything else a person reads goes to <paramref name="error"/>.</remarks>
    public static int Run(IReadOnlyList<string> args, Profile profile, string keysFile, TextWriter output, TextWriter error)
    {
        var declared = profile.Http?.Accounts ?? [];
        var keys = new AccountKeyStore(keysFile);
        switch (args.ToArray())
        {
            case ["add", var account, .. var options]:
                if (declared.All(a => a.Name != account))
                {
                    error.WriteLine($"no account '{account}' in the profile's http.accounts" + (declared.Count == 0 ? "; declare it there first" : $" (declared: {string.Join(", ", declared.Select(a => a.Name))})"));
                    return 2;
                }
                var label = options is ["--label", var text] ? text : null;
                if (options.Length > 0 && label is null)
                {
                    error.WriteLine(Usage);
                    return 2;
                }
                var (created, key) = keys.Create(account, label);
                output.WriteLine(key);
                error.WriteLine($"key {created} for account '{account}'. This is the only time it is shown: chargehand stores a salted hash. Send it as 'Authorization: Bearer <key>'.");
                return 0;
            case ["list"]:
                output.WriteLine($"{AccountSettings.Owner}  the key of http.api_key_secret; every run, no caps");
                var all = keys.List();
                foreach (var account in declared)
                {
                    output.WriteLine($"{account.Name}  {Caps(account)}");
                    var own = all.Where(k => k.Account == account.Name).ToList();
                    if (own.Count == 0)
                        output.WriteLine("  (no keys)");
                    foreach (var k in own)
                        output.WriteLine($"  {k.Id}  {k.Created:yyyy-MM-dd}  {(k.Revoked is { } at ? $"revoked {at:yyyy-MM-dd}" : "active")}  {k.Label}".TrimEnd());
                }
                foreach (var k in all.Where(k => declared.All(a => a.Name != k.Account)))
                    output.WriteLine($"{k.Account}  not in the profile: its keys open nothing\n  {k.Id}  {k.Created:yyyy-MM-dd}  {(k.Revoked is null ? "active" : "revoked")}");
                return 0;
            case ["revoke", var id]:
                if (keys.Revoke(id))
                {
                    error.WriteLine($"key {id} revoked");
                    return 0;
                }
                error.WriteLine($"no active key {id}");
                return 1;
            default:
                error.WriteLine(Usage);
                return 2;
        }
    }

    private static string Caps(AccountSettings a)
    {
        var caps = new List<string>();
        void Add(string name, object? value, string unit = "")
        {
            if (value is not null)
                caps.Add($"{name} {Convert.ToString(value, CultureInfo.InvariantCulture)}{unit}");
        }
        Add("usd/day", a.MaxUsdPerDay);
        Add("usd/month", a.MaxUsdPerMonth);
        Add("tokens/day", a.MaxTokensPerDay);
        Add("tokens/month", a.MaxTokensPerMonth);
        Add("concurrent", a.MaxConcurrentRuns);
        caps.Add(a.Driven ? "driven allowed" : "no driven");
        if (a.Repositories is { Count: > 0 })
            caps.Add($"{a.Repositories.Count} repositories");
        return string.Join(", ", caps);
    }
}
