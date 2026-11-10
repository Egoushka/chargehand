using Chargehand.Modules.Accounts.Api;

namespace Chargehand.Modules.Accounts.Tests;

public class AccountsCliTests
{
    [Fact]
    public void The_accounts_commands_add_list_and_revoke_without_ever_listing_a_key()
    {
        using var dir = new TempDir();
        var keys = Path.Combine(dir.Path, "accounts.json");
        AccountSettings[] declared = [new("alice", 5m, null, null, null, null, true, null, AllowServerCredential: true)];
        int Run(out string stdout, out string stderr, params string[] args)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = AccountsCli.Run(args, declared, keys, output, error);
            (stdout, stderr) = (output.ToString(), error.ToString());
            return code;
        }

        Assert.Equal(2, Run(out _, out var unknown, "add", "mallory"));
        Assert.Contains("alice", unknown, StringComparison.Ordinal);
        Assert.Equal(2, Run(out _, out _, "add", "owner"));
        Assert.Equal(0, Run(out var key, out var notice, "add", "alice", "--label", "laptop"));
        Assert.Matches(@"^chk1\.[0-9a-f]{16}\.[A-Za-z0-9_-]{43}\r?\n$", key);
        var id = key.Split('.')[1];
        Assert.Contains(id, notice, StringComparison.Ordinal);

        Assert.Equal(0, Run(out var list, out _, "list"));
        Assert.DoesNotContain(key.Trim().Split('.')[2], list, StringComparison.Ordinal);
        Assert.Contains($"{id}", list, StringComparison.Ordinal);
        Assert.Contains("laptop", list, StringComparison.Ordinal);
        Assert.Contains("usd/day 5", list, StringComparison.Ordinal);
        Assert.Contains("server's model key", list, StringComparison.Ordinal);
        Assert.Contains("active", list, StringComparison.Ordinal);

        Assert.Equal(0, Run(out _, out _, "revoke", id));
        Assert.Equal(1, Run(out _, out _, "revoke", id));
        Run(out var again, out _, "list");
        Assert.Contains("revoked", again, StringComparison.Ordinal);
        Assert.Equal(2, Run(out _, out _, "bogus"));
    }
}
