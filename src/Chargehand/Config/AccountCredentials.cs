using Chargehand.Contracts;

namespace Chargehand.Config;

/// <summary>Whose model credential a run uses (ADR 0052): the owner's runs, a CLI run and an account that may use it take the server's; an account with a <c>model</c> block takes its own;
/// any other account is refused. Pure over the profile's accounts, so the server, the runtimes and the driven runner decide the same way.</summary>
public sealed class AccountCredentials(IReadOnlyList<AccountSettings>? accounts)
{
    /// <summary>Why <paramref name="account"/> may not start a run, or null when it has a credential to run on.</summary>
    public static string? Refusal(AccountSettings account) => account.Model is null && !account.AllowServerCredential
        ? $"account '{account.Name}' has no model credential of its own and may not use the server's; set http.accounts[].model, or allow_server_credential to spend the server's"
        : null;

    /// <summary>The account's own credential; null: the server's. Throws <see cref="ChargehandException"/> (<c>credential_unavailable</c>) for an account that may use neither, or
    /// that the profile no longer declares (a batch a restart took up).</summary>
    public AccountModel? For(string? actor)
    {
        if (actor is null or AccountSettings.Owner)
            return null;
        var account = accounts?.FirstOrDefault(a => a.Name == actor)
            ?? throw new ChargehandException(ErrorCode.CredentialUnavailable, $"account '{actor}' is not declared in http.accounts", "Declare the account again, or start the run with another key.");
        return Refusal(account) is { } refusal
            ? throw new ChargehandException(ErrorCode.CredentialUnavailable, refusal, "Set model on the account in the profile, or set allow_server_credential to true.")
            : account.Model;
    }
}
