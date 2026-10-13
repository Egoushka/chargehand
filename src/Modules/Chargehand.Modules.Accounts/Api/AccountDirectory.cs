namespace Chargehand.Modules.Accounts.Api;

/// <summary>The accounts the profile declares, and the keys that open them. A key whose account the profile no longer declares opens nothing. With <paramref name="use"/>, the time a key opens a request is kept.</summary>
public sealed class AccountDirectory(IReadOnlyList<AccountSettings> declared, AccountKeyStore keys, AccountKeyUse? use = null)
{
    public Principal? Authenticate(string bearer)
    {
        if (keys.Authenticate(bearer) is not { } key || declared.FirstOrDefault(a => a.Name == key.Account) is not { } account)
            return null;
        use?.Record(key.Id);
        return new Principal(account.Name, account);
    }
}
