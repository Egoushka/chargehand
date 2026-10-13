namespace Chargehand.Modules.Accounts.Api;

/// <summary>Who a request is (ADR 0050). The owner is the holder of <c>http.api_key_secret</c>; any other name is an account the profile declares, and
/// <paramref name="Account"/> holds its rules. No principal at all (stdio, the CLI) is unrestricted.</summary>
public sealed record Principal(string Name, AccountSettings? Account = null)
{
    public static Principal Owner { get; } = new(AccountSettings.Owner);

    public bool IsOwner => Name == AccountSettings.Owner;

    /// <summary>The owner sees every run; an account sees the runs it started. A run with no recorded actor predates accounts and is the owner's.</summary>
    public static bool Sees(Principal? who, string? actor) => who is null || who.IsOwner || who.Name == actor;
}
